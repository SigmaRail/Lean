/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// IDataProvider that materializes LEAN equity daily zip files on-demand from ClickHouse.
    ///
    /// Intended to be used via CompositeDataProvider:
    /// - DefaultDataProvider (disk)
    /// - ClickHouseDataProvider (on-demand)
    ///
    /// Supports overwrite-on-miss based on range coverage + ClickHouse max(date) and max(lastupdated).
    /// </summary>
    public class ClickHouseDataProvider : BaseDownloaderDataProvider
    {
        private readonly HttpClient _http;
        private readonly string _host;
        private readonly int _port;
        private readonly string _database;
        private readonly string _user;
        private readonly string _password;

        private readonly bool _materialize;
        private readonly int _historyPadDays;
        private readonly DateTime _startDate;
        private readonly DateTime _endDate;

        private static readonly ConcurrentDictionary<string, byte[]> _zipBytesCache = new();
        private readonly bool _trace;

        /// <summary>
        /// The most-recently-constructed provider instance, exposed so Python-side
        /// scheduling can drive windowed pre-hydration / eviction (see
        /// <see cref="PreHydrate"/> and <see cref="Evict"/>).
        /// </summary>
        public static ClickHouseDataProvider Current { get; private set; }

        public ClickHouseDataProvider()
        {
            Current = this;
            _host = Config.Get("clickhouse-host", "localhost");
            _port = Config.GetInt("clickhouse-http-port", 8123);
            _database = Config.Get("clickhouse-database", string.Empty);
            _user = Config.Get("clickhouse-user", string.Empty);
            _password = Config.Get("clickhouse-password", string.Empty);

            var dataMode = Config.Get("clickhouse-data-mode", Config.Get("data-mode", string.Empty)).ToLowerInvariant();
            if (dataMode == "in_memory" || dataMode == "in-memory" || dataMode == "ram")
            {
                _materialize = false;
            }
            else if (dataMode == "disk_cache" || dataMode == "disk-cache" || dataMode == "disk")
            {
                _materialize = true;
            }
            else
            {
                _materialize = Config.GetBool("clickhouse-materialize", false);
            }

            _trace = Config.GetBool("clickhouse-trace", false);

            // Calendar days padding. Default 365 ~= 250 trading days.
            _historyPadDays = Config.GetInt("clickhouse-history-pad-days", 365);

            // Provider needs explicit config, since it can't access QCAlgorithm.StartDate/EndDate.
            _startDate = ParseIsoDate(Config.Get("clickhouse-backtest-start", "1998-01-01"));
            _endDate = ParseIsoDate(Config.Get("clickhouse-backtest-end", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Config.GetInt("clickhouse-timeout-seconds", 60))
            };

            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider(): dataMode={(_materialize ? "disk_cache" : "in_memory")} host={_host}:{_port} db={_database} padDays={_historyPadDays} range={_startDate:yyyy-MM-dd}->{_endDate:yyyy-MM-dd}");
            }
        }

        public override Stream Fetch(string key)
        {
            var normalizedKey = NormalizeKey(key);

            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider.Fetch(): key='{key}' normalized='{normalizedKey}'");
            }

            if (!IsSupportedKey(normalizedKey))
            {
                return null;
            }

            if (_zipBytesCache.TryGetValue(normalizedKey, out var cached))
            {
                if (_trace)
                {
                    Log.Trace($"ClickHouseDataProvider.Fetch(): cache HIT {normalizedKey} ({cached.Length} bytes)");
                }
                return new MemoryStream(cached, writable: false);
            }

            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider.Fetch(): cache MISS {normalizedKey}");
            }

            return DownloadOnce(normalizedKey, BuildAndOptionallyPersist);
        }

        /// <summary>
        /// Override GetStream to serve from the in-memory zip cache instead of reading from disk.
        /// BaseDownloaderDataProvider calls GetStream after BuildAndOptionallyPersist completes.
        /// Without this override, GetStream delegates to DefaultDataProvider.Fetch() which reads
        /// from disk — but in in_memory mode the zip was never written to disk, so it returns null
        /// and the 609/622 failure rate seen in the backtest log.
        /// </summary>
        protected override Stream GetStream(string key)
        {
            var normalizedKey = NormalizeKey(key);
            if (_zipBytesCache.TryGetValue(normalizedKey, out var cached))
            {
                return new MemoryStream(cached, writable: false);
            }
            return base.Fetch(key);
        }

        protected override bool NeedToDownload(string filePath)
        {
            var normalizedKey = NormalizeKey(filePath);

            // In-Memory RAM Mode: if already cached in RAM, return false immediately.
            if (!_materialize)
            {
                return !_zipBytesCache.ContainsKey(normalizedKey);
            }

            var normalizedPath = FileExtension.ToNormalizedPath(filePath);
            var metaPath = normalizedPath + ".meta.json";

            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider.NeedToDownload(): filePath='{filePath}' normalizedPath='{normalizedPath}'");
            }

            if (!File.Exists(normalizedPath) || !File.Exists(metaPath))
            {
                if (_trace)
                {
                    Log.Trace($"ClickHouseDataProvider.NeedToDownload(): missing file/meta for '{normalizedPath}'");
                }
                return true;
            }

            ClickHouseZipMeta meta;
            try
            {
                meta = JsonConvert.DeserializeObject<ClickHouseZipMeta>(File.ReadAllText(metaPath));
            }
            catch
            {
                return true;
            }

            var ticker = ParseTickerFromKey(filePath);
            if (meta == null || !string.Equals(meta.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var range = GetEffectiveRange();

            // Coverage check
            if (meta.EffectiveStart > range.Start || meta.EffectiveEnd < range.End)
            {
                if (_trace)
                {
                    Log.Trace($"ClickHouseDataProvider.NeedToDownload(): coverage miss ticker={ticker} meta=[{meta.EffectiveStart:yyyy-MM-dd}->{meta.EffectiveEnd:yyyy-MM-dd}] required=[{range.Start:yyyy-MM-dd}->{range.End:yyyy-MM-dd}]");
                }
                return true;
            }

            try
            {
                var db = QueryDbMaxes(ticker, range.Start, range.End);

                // If DB has no data in range, don't force rebuild
                if (!db.DbMaxDate.HasValue)
                {
                    return false;
                }

                if (!meta.DbMaxDate.HasValue || meta.DbMaxDate.Value.Date < db.DbMaxDate.Value.Date)
                {
                    if (_trace)
                    {
                        Log.Trace($"ClickHouseDataProvider.NeedToDownload(): db max(date) newer ticker={ticker} meta={meta.DbMaxDate:yyyy-MM-dd} db={db.DbMaxDate:yyyy-MM-dd}");
                    }
                    return true;
                }

                if (meta.DbLastUpdated < db.DbLastUpdated)
                {
                    if (_trace)
                    {
                        Log.Trace($"ClickHouseDataProvider.NeedToDownload(): db max(lastupdated) newer ticker={ticker} meta={meta.DbLastUpdated:o} db={db.DbLastUpdated:o}");
                    }
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                Log.Error(e);
                // If ClickHouse isn't reachable, fall back to existing on-disk file
                return false;
            }
        }

        /// <summary>
        /// Bulk pre-hydrates in-memory zip bytes for multiple tickers in a single ClickHouse TabSeparated query.
        /// When <paramref name="start"/>/<paramref name="end"/> are omitted, the effective backtest range is used.
        /// </summary>
        public int PreHydrate(IEnumerable<string> tickers, DateTime? start = null, DateTime? end = null)
        {
            if (tickers == null || string.IsNullOrEmpty(_database))
            {
                return 0;
            }

            var unCached = new List<string>();
            foreach (var t in tickers)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                var upper = t.Trim().ToUpperInvariant();
                var key = $"equity/usa/daily/{upper.ToLowerInvariant()}.zip";
                if (!_zipBytesCache.ContainsKey(key))
                {
                    unCached.Add(upper);
                }
            }

            if (unCached.Count == 0)
            {
                return 0;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var range = GetEffectiveRange();
            var rangeStart = start ?? range.Start;
            var rangeEnd = end ?? range.End;
            var tickerFilter = string.Join("','", unCached);

            var sql = $@"
                SELECT ticker, concat(
                    formatDateTime(toDate(date), '%Y%m%d 00:00'), ',',
                    toString(round(ifNull(open, close) * 10000)), ',',
                    toString(round(ifNull(high, close) * 10000)), ',',
                    toString(round(ifNull(low, close) * 10000)), ',',
                    toString(round(ifNull(close, 0) * 10000)), ',',
                    toString(toUInt64(ifNull(volume, 0)))
                )
                FROM screener_price_tech_daily FINAL
                WHERE ticker IN ('{tickerFilter}')
                  AND date BETWEEN toDate('{rangeStart:yyyy-MM-dd}') AND toDate('{rangeEnd:yyyy-MM-dd}')
                  AND close IS NOT NULL
                  AND volume IS NOT NULL
                ORDER BY ticker ASC, date ASC
                FORMAT TabSeparated
            ";

            var rawBytes = QueryRawBytes(sql);
            if (_trace)
            {
                var head = rawBytes != null && rawBytes.Length > 0
                    ? Encoding.UTF8.GetString(rawBytes, 0, Math.Min(160, rawBytes.Length)).Replace("\n", "\\n")
                    : "";
                Log.Trace($"ClickHouseDataProvider.PreHydrate(): rawBytes={rawBytes?.Length ?? -1} head={head}");
            }
            if (rawBytes == null || rawBytes.Length == 0)
            {
                return 0;
            }

            // Group lines by ticker
            var tickerLines = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StreamReader(new MemoryStream(rawBytes), Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var tabIdx = line.IndexOf('\t');
                    if (tabIdx <= 0) continue;

                    var symbol = line.Substring(0, tabIdx).Trim();
                    var csvLine = line.Substring(tabIdx + 1);

                    if (!tickerLines.TryGetValue(symbol, out var sb))
                    {
                        sb = new StringBuilder();
                        tickerLines[symbol] = sb;
                    }
                    sb.AppendLine(csvLine);
                }
            }

            var count = 0;
            foreach (var kvp in tickerLines)
            {
                var symbol = kvp.Key.ToUpperInvariant();
                var key = $"equity/usa/daily/{symbol.ToLowerInvariant()}.zip";
                var csvBytes = Encoding.UTF8.GetBytes(kvp.Value.ToString());

                using var ms = new MemoryStream();
                using (var zip = new Ionic.Zip.ZipFile())
                {
                    zip.AddEntry($"{symbol.ToLowerInvariant()}.csv", csvBytes);
                    zip.Save(ms);
                }

                var zipBytes = ms.ToArray();
                _zipBytesCache[key] = zipBytes;
                count++;

                if (_materialize)
                {
                    try
                    {
                        var normalizedPath = FileExtension.ToNormalizedPath(key);
                        var dir = Path.GetDirectoryName(normalizedPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(normalizedPath, zipBytes);
                    }
                    catch { }
                }
            }

            sw.Stop();
            if (_trace || count > 0)
            {
                Log.Trace($"ClickHouseDataProvider.PreHydrate(): hydrated {count}/{unCached.Count} symbols in {sw.ElapsedMilliseconds}ms (in-memory mode={!_materialize})");
            }

            return count;
        }

        /// <summary>
        /// Removes cached zip bytes for a single ticker (position-driven eviction on flat).
        /// </summary>
        public void Evict(string ticker)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                return;
            }
            var key = $"equity/usa/daily/{ticker.Trim().ToLowerInvariant()}.zip";
            _zipBytesCache.TryRemove(key, out _);
        }

        /// <summary>
        /// Removes cached zip bytes for multiple tickers.
        /// </summary>
        public void EvictMany(IEnumerable<string> tickers)
        {
            if (tickers == null)
            {
                return;
            }
            foreach (var t in tickers)
            {
                Evict(t);
            }
        }

        private void BuildAndOptionallyPersist(string key)
        {
            if (string.IsNullOrEmpty(_database))
            {
                throw new InvalidOperationException("ClickHouseDataProvider requires 'clickhouse-database' config to be set");
            }

            var ticker = ParseTickerFromKey(key);
            var range = GetEffectiveRange();

            if (_trace && _materialize)
            {
                Log.Trace($"ClickHouseDataProvider.Build(): ticker={ticker} range=[{range.Start:yyyy-MM-dd}->{range.End:yyyy-MM-dd}] key='{key}'");
            }

            var zipBytes = FetchDirectDailyEquityZipBytes(ticker, range.Start, range.End);

            _zipBytesCache[key] = zipBytes;

            if (!_materialize)
            {
                return;
            }

            var db = QueryDbMaxes(ticker, range.Start, range.End);
            var normalizedPath = FileExtension.ToNormalizedPath(key);
            var dir = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(normalizedPath, zipBytes);

            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider.Build(): wrote {zipBytes.Length} bytes to '{normalizedPath}'");
            }

            var meta = new ClickHouseZipMeta
            {
                Ticker = ticker,
                EffectiveStart = range.Start,
                EffectiveEnd = range.End,
                DbMaxDate = db.DbMaxDate,
                DbLastUpdated = db.DbLastUpdated,
                BuiltUtc = DateTime.UtcNow
            };

            File.WriteAllText(normalizedPath + ".meta.json", JsonConvert.SerializeObject(meta));
        }

        private byte[] FetchDirectDailyEquityZipBytes(string ticker, DateTime start, DateTime end)
        {
            var sql = $@"
                SELECT concat(
                    formatDateTime(toDate(date), '%Y%m%d 00:00'), ',',
                    toString(round(ifNull(open, close) * 10000)), ',',
                    toString(round(ifNull(high, close) * 10000)), ',',
                    toString(round(ifNull(low, close) * 10000)), ',',
                    toString(round(ifNull(close, 0) * 10000)), ',',
                    toString(toUInt64(ifNull(volume, 0)))
                )
                FROM screener_price_tech_daily FINAL
                WHERE ticker = '{ticker}'
                  AND date BETWEEN toDate('{start:yyyy-MM-dd}') AND toDate('{end:yyyy-MM-dd}')
                  AND close IS NOT NULL
                  AND volume IS NOT NULL
                ORDER BY date ASC
                FORMAT TabSeparated
            ";

            var csvBytes = QueryRawBytes(sql);
            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider.FetchDirect(): ticker={ticker} range={start:yyyy-MM-dd}->{end:yyyy-MM-dd} rawBytes={(csvBytes?.Length ?? -1)}");
            }
            var lower = ticker.ToLowerInvariant();
            var entryName = $"{lower}.csv";

            using var ms = new MemoryStream();
            using (var zip = new Ionic.Zip.ZipFile())
            {
                zip.AddEntry(entryName, csvBytes);
                zip.Save(ms);
            }
            return ms.ToArray();
        }

        private byte[] QueryRawBytes(string sql)
        {
            try
            {
                var url = $"http://{_host}:{_port}/?database={Uri.EscapeDataString(_database)}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(sql, Encoding.UTF8, "text/plain");

                if (!string.IsNullOrEmpty(_user)) req.Headers.Add("X-ClickHouse-User", _user);
                if (!string.IsNullOrEmpty(_password)) req.Headers.Add("X-ClickHouse-Key", _password);

                using var resp = _http.Send(req);
                resp.EnsureSuccessStatusCode();

                return resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Error($"ClickHouseDataProvider.QueryRawBytes(): {e.GetType().Name}: {e.Message}");
                throw;
            }
        }

        private static bool IsSupportedKey(string key)
        {
            var k = key.Replace('\\', '/').ToLowerInvariant();
            return k.StartsWith("equity/usa/daily/") && k.EndsWith(".zip");
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }

            // LEAN sometimes requests keys with a leading '/', but FileExtension.ToNormalizedPath
            // treats such paths as absolute. We always want paths relative to Globals.DataFolder.
            key = key.TrimStart('/', '\\').Replace('\\', '/');

            // Docker Lean CLI mounts often prefix requests with Lean/Data/ or Data/.
            foreach (var prefix in new[] { "Lean/Data/", "Data/" })
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    key = key.Substring(prefix.Length);
                    break;
                }
            }

            return key;
        }

        private static string ParseTickerFromKey(string key)
        {
            key = NormalizeKey(key).Replace('\\', '/');
            var file = Path.GetFileNameWithoutExtension(key);
            return file.ToUpperInvariant();
        }

        private (DateTime Start, DateTime End) GetEffectiveRange()
        {
            var start = _startDate.Date.AddDays(-_historyPadDays);
            if (start < new DateTime(1900, 1, 1)) start = new DateTime(1900, 1, 1);
            return (start, _endDate.Date);
        }

        private (DateTime? DbMaxDate, DateTime DbLastUpdated) QueryDbMaxes(string ticker, DateTime start, DateTime end)
        {
            var sql = $@"
                SELECT
                    max(toDate(date)) AS max_date,
                    max(lastupdated) AS max_lastupdated
                FROM screener_price_tech_daily FINAL
                WHERE ticker = '{ticker}'
                  AND date BETWEEN toDate('{start:yyyy-MM-dd}') AND toDate('{end:yyyy-MM-dd}')
                FORMAT JSONEachRow
            ";

            var rows = QueryJsonEachRow(sql);
            if (rows.Count == 0)
            {
                return (null, DateTime.MinValue);
            }

            var row = rows[0];

            DateTime? maxDate = null;
            if (row.TryGetValue("max_date", out var maxDateObj) && maxDateObj != null)
            {
                var s = maxDateObj.ToString();
                if (!string.IsNullOrEmpty(s))
                {
                    maxDate = DateTime.Parse(s, CultureInfo.InvariantCulture).Date;
                }
            }

            var maxLastUpdated = DateTime.MinValue;
            if (row.TryGetValue("max_lastupdated", out var maxUpdatedObj) && maxUpdatedObj != null)
            {
                var s = maxUpdatedObj.ToString();
                if (!string.IsNullOrEmpty(s))
                {
                    maxLastUpdated = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
                }
            }

            return (maxDate, maxLastUpdated);
        }

        private List<DailyBarRow> QueryDailyBars(string ticker, DateTime start, DateTime end)
        {
            var sql = $@"
                SELECT
                    toDate(date) AS date,
                    ifNull(open, close) AS open,
                    ifNull(high, close) AS high,
                    ifNull(low, close) AS low,
                    ifNull(close, 0) AS close,
                    ifNull(volume, 0) AS volume
                FROM screener_price_tech_daily FINAL
                WHERE ticker = '{ticker}'
                  AND date BETWEEN toDate('{start:yyyy-MM-dd}') AND toDate('{end:yyyy-MM-dd}')
                  AND close IS NOT NULL
                  AND volume IS NOT NULL
                ORDER BY date ASC
                FORMAT JSONEachRow
            ";

            var rows = QueryJsonEachRow(sql);
            var result = new List<DailyBarRow>(rows.Count);
            foreach (var r in rows)
            {
                result.Add(new DailyBarRow
                {
                    Date = DateTime.Parse(r["date"].ToString(), CultureInfo.InvariantCulture).Date,
                    Open = Convert.ToDecimal(r["open"], CultureInfo.InvariantCulture),
                    High = Convert.ToDecimal(r["high"], CultureInfo.InvariantCulture),
                    Low = Convert.ToDecimal(r["low"], CultureInfo.InvariantCulture),
                    Close = Convert.ToDecimal(r["close"], CultureInfo.InvariantCulture),
                    Volume = Convert.ToInt64(r["volume"], CultureInfo.InvariantCulture)
                });
            }

            return result;
        }

        private List<Dictionary<string, object>> QueryJsonEachRow(string sql)
        {
            var url = $"http://{_host}:{_port}/?database={Uri.EscapeDataString(_database)}&default_format=JSONEachRow";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(sql, Encoding.UTF8, "text/plain");

            if (!string.IsNullOrEmpty(_user)) req.Headers.Add("X-ClickHouse-User", _user);
            if (!string.IsNullOrEmpty(_password)) req.Headers.Add("X-ClickHouse-Key", _password);

            using var resp = _http.Send(req);
            resp.EnsureSuccessStatusCode();

            var payload = resp.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            var rows = new List<Dictionary<string, object>>();

            foreach (var line in payload.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                rows.Add(JsonConvert.DeserializeObject<Dictionary<string, object>>(trimmed));
            }

            return rows;
        }

        private static byte[] BuildDailyEquityZipBytes(string ticker, List<DailyBarRow> rows)
        {
            var lower = ticker.ToLowerInvariant();
            var entryName = $"{lower}.csv";

            var sb = new StringBuilder(rows.Count * 64);
            foreach (var r in rows)
            {
                // LEAN daily/hour equities use: yyyyMMdd HH:mm, scaled O,H,L,C, volume
                var ts = r.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + " 00:00";
                sb.Append(ts);
                sb.Append(',');
                sb.Append(ScalePrice(r.Open));
                sb.Append(',');
                sb.Append(ScalePrice(r.High));
                sb.Append(',');
                sb.Append(ScalePrice(r.Low));
                sb.Append(',');
                sb.Append(ScalePrice(r.Close));
                sb.Append(',');
                sb.Append(r.Volume);
                sb.Append('\n');
            }

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                writer.Write(sb.ToString());
            }
            return ms.ToArray();
        }

        private static int ScalePrice(decimal p)
        {
            return (int)Math.Round(p * 10000m, MidpointRounding.AwayFromZero);
        }

        private static DateTime ParseIsoDate(string value)
        {
            return DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private sealed class DailyBarRow
        {
            public DateTime Date;
            public decimal Open;
            public decimal High;
            public decimal Low;
            public decimal Close;
            public long Volume;
        }

        private sealed class ClickHouseZipMeta
        {
            public string Ticker { get; set; }
            public DateTime EffectiveStart { get; set; }
            public DateTime EffectiveEnd { get; set; }
            public DateTime? DbMaxDate { get; set; }
            public DateTime DbLastUpdated { get; set; }
            public DateTime BuiltUtc { get; set; }
        }
    }
}
