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

        private readonly ConcurrentDictionary<string, byte[]> _zipBytesCache = new();
        private readonly bool _trace;

        public ClickHouseDataProvider()
        {
            _host = Config.Get("clickhouse-host", "localhost");
            _port = Config.GetInt("clickhouse-http-port", 8123);
            _database = Config.Get("clickhouse-database", string.Empty);
            _user = Config.Get("clickhouse-user", string.Empty);
            _password = Config.Get("clickhouse-password", string.Empty);

            _materialize = Config.GetBool("clickhouse-materialize", false);
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
                Log.Trace($"ClickHouseDataProvider(): enabled materialize={_materialize} host={_host}:{_port} db={_database} padDays={_historyPadDays} range={_startDate:yyyy-MM-dd}->{_endDate:yyyy-MM-dd}");
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
                return new MemoryStream(cached, writable: false);
            }

            return DownloadOnce(normalizedKey, BuildAndOptionallyPersist);
        }

        protected override bool NeedToDownload(string filePath)
        {
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

        private void BuildAndOptionallyPersist(string key)
        {
            if (string.IsNullOrEmpty(_database))
            {
                throw new InvalidOperationException("ClickHouseDataProvider requires 'clickhouse-database' config to be set");
            }

            var ticker = ParseTickerFromKey(key);
            var range = GetEffectiveRange();

            if (_trace)
            {
                Log.Trace($"ClickHouseDataProvider.Build(): ticker={ticker} range=[{range.Start:yyyy-MM-dd}->{range.End:yyyy-MM-dd}] key='{key}'");
            }

            var db = QueryDbMaxes(ticker, range.Start, range.End);
            var bars = QueryDailyBars(ticker, range.Start, range.End);
            var zipBytes = BuildDailyEquityZipBytes(ticker, bars);

            _zipBytesCache[key] = zipBytes;

            if (!_materialize)
            {
                return;
            }

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
            return key.TrimStart('/', '\\');
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
                FROM daily_screener
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
                    ifNull(`dm.open`, close) AS open,
                    ifNull(`dm.high`, close) AS high,
                    ifNull(`dm.low`, close) AS low,
                    ifNull(close, 0) AS close,
                    ifNull(volume, 0) AS volume
                FROM daily_screener
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
