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
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using QuantConnect.Logging;
using QuantConnect.Interfaces;

namespace QuantConnect.Data.UniverseSelection
{
    /// <summary>
    /// ClickHouse universe data provider with intelligent caching.
    /// 
    /// Pre-fetches and caches all universe data for the algorithm's date range
    /// during initialization for O(1) time slice lookups.
    /// 
    /// Works on both local LEAN (with ClickHouse) and QuantConnect Cloud (with LEAN's data sources)
    /// </summary>
    public class ClickHouseUniverseProvider
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _database;
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _httpClient;
        
        // Cache: Date -> List of results
        private readonly Dictionary<DateTime, List<Dictionary<string, object>>> _cache;
        private bool _cacheInitialized;

        /// <summary>
        /// Gets whether ClickHouse is available (configured and reachable)
        /// </summary>
        public bool IsClickHouseAvailable { get; private set; }

        /// <summary>
        /// Initializes a new instance of the ClickHouseUniverseProvider
        /// </summary>
        /// <param name="algorithm">The algorithm instance to get date range from</param>
        /// <param name="host">ClickHouse server host</param>
        /// <param name="port">ClickHouse HTTP port</param>
        /// <param name="database">Database name</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        public ClickHouseUniverseProvider(
            IAlgorithm algorithm,
            string host = null,
            int? port = null,
            string database = null,
            string username = null,
            string password = null)
        {
            _cache = new Dictionary<DateTime, List<Dictionary<string, object>>>();
            _cacheInitialized = false;
            _httpClient = new HttpClient();
            
            // Get configuration from config.json or use defaults
            _host = host ?? Config.Get("clickhouse-host", "localhost");
            _port = port ?? Config.GetInt("clickhouse-port", 8123);
            _database = database ?? Config.Get("clickhouse-database", "vera");
            _username = username ?? Config.Get("clickhouse-username", "default");
            _password = password ?? Config.Get("clickhouse-password", "");

            // Test ClickHouse connectivity
            IsClickHouseAvailable = TestConnection();
            
            if (!IsClickHouseAvailable)
            {
                Log.Trace("ClickHouseUniverseProvider: ClickHouse not available, falling back to LEAN data sources");
            }
            else
            {
                // Pre-fetch all universe data for the algorithm's date range
                Log.Trace($"ClickHouseUniverseProvider: Caching universe data from {algorithm.StartDate:yyyy-MM-dd} to {algorithm.EndDate:yyyy-MM-dd}...");
                InitializeCache(algorithm.StartDate, algorithm.EndDate);
            }
        }

        /// <summary>
        /// Gets sector universe for a specific date (uses cache)
        /// </summary>
        public List<Dictionary<string, object>> GetSectorUniverse(DateTime date, string sector, int topN = 20)
        {
            if (IsClickHouseAvailable && _cacheInitialized)
            {
                // Use cached data
                return GetFromCache(date, row => 
                    row.ContainsKey("sector") && 
                    row["sector"]?.ToString() == sector, 
                    topN);
            }
            
            if (IsClickHouseAvailable)
            {
                // Query ClickHouse directly (for dates not in cache)
                return QuerySectorFromClickHouse(date, sector, topN);
            }
            
            // Fallback: Return empty (LEAN will use its own data sources)
            return new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Gets industry universe for a specific date
        /// </summary>
        public List<Dictionary<string, object>> GetIndustryUniverse(DateTime date, string industry, int topN = 10)
        {
            if (IsClickHouseAvailable && _cacheInitialized)
            {
                return GetFromCache(date, row => 
                    row.ContainsKey("industry") && 
                    row["industry"]?.ToString() == industry, 
                    topN);
            }
            
            if (IsClickHouseAvailable)
            {
                return QueryIndustryFromClickHouse(date, industry, topN);
            }
            
            return new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Gets dollar volume universe for a specific date
        /// </summary>
        public List<Dictionary<string, object>> GetDollarVolumeUniverse(DateTime date, int topN = 100)
        {
            if (IsClickHouseAvailable && _cacheInitialized)
            {
                return GetFromCache(date, row => true, topN); // All cached entries are already filtered by dollar_volume_rank
            }
            
            if (IsClickHouseAvailable)
            {
                return QueryDollarVolumeFromClickHouse(date, topN);
            }
            
            return new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Initialize cache with all universe data for the backtest period
        /// </summary>
        private void InitializeCache(DateTime startDate, DateTime endDate)
        {
            try
            {
                Log.Trace($"ClickHouseDualModeProvider: Caching universe data from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
                
                // Query all trading days at once with a date range
                var query = $@"
                    SELECT date, ticker, close, volume, dollar_volume, pe_ratio, pb_ratio, 
                           marketcap, sector, industry, dollar_volume_rank
                    FROM daily_screener FINAL
                    WHERE date >= '{startDate:yyyy-MM-dd}'
                      AND date <= '{endDate:yyyy-MM-dd}'
                      AND close > 0
                      AND volume > 0
                    ORDER BY date, dollar_volume_rank
                    FORMAT JSONEachRow";

                var results = ExecuteQuery(query);
                
                // Group by date into cache
                foreach (var row in results)
                {
                    if (row.ContainsKey("date"))
                    {
                        var rowDate = DateTime.Parse(row["date"].ToString());
                        if (!_cache.ContainsKey(rowDate))
                        {
                            _cache[rowDate] = new List<Dictionary<string, object>>();
                        }
                        _cache[rowDate].Add(row);
                    }
                }
                
                _cacheInitialized = true;
                Log.Trace($"ClickHouseDualModeProvider: Cached data for {_cache.Count} dates, {results.Count} total rows");
            }
            catch (Exception ex)
            {
                Log.Error($"ClickHouseDualModeProvider: Failed to initialize cache: {ex.Message}");
                IsClickHouseAvailable = false;
            }
        }

        /// <summary>
        /// Get filtered data from cache
        /// </summary>
        private List<Dictionary<string, object>> GetFromCache(
            DateTime date, 
            Func<Dictionary<string, object>, bool> filter, 
            int limit)
        {
            if (!_cache.ContainsKey(date))
            {
                return new List<Dictionary<string, object>>();
            }

            return _cache[date]
                .Where(filter)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Query sector universe from ClickHouse
        /// </summary>
        private List<Dictionary<string, object>> QuerySectorFromClickHouse(DateTime date, string sector, int topN)
        {
            var query = $@"
                SELECT ticker, close, volume, dollar_volume, pe_ratio, pb_ratio,
                       marketcap, sector, industry, dollar_volume_rank
                FROM daily_screener FINAL
                WHERE date = '{date:yyyy-MM-dd}'
                  AND close > 0
                  AND volume > 0
                  AND sector = '{sector}'
                ORDER BY dollar_volume_rank
                LIMIT {topN}
                FORMAT JSONEachRow";

            return ExecuteQuery(query);
        }

        /// <summary>
        /// Query industry universe from ClickHouse
        /// </summary>
        private List<Dictionary<string, object>> QueryIndustryFromClickHouse(DateTime date, string industry, int topN)
        {
            var query = $@"
                SELECT ticker, close, volume, dollar_volume, pe_ratio, pb_ratio,
                       marketcap, sector, industry, dollar_volume_rank
                FROM daily_screener FINAL
                WHERE date = '{date:yyyy-MM-dd}'
                  AND close > 0
                  AND volume > 0
                  AND industry = '{industry}'
                ORDER BY dollar_volume_rank
                LIMIT {topN}
                FORMAT JSONEachRow";

            return ExecuteQuery(query);
        }

        /// <summary>
        /// Query dollar volume universe from ClickHouse
        /// </summary>
        private List<Dictionary<string, object>> QueryDollarVolumeFromClickHouse(DateTime date, int topN)
        {
            var query = $@"
                SELECT ticker, close, volume, dollar_volume, pe_ratio, pb_ratio, 
                       marketcap, sector, industry, dollar_volume_rank
                FROM daily_screener FINAL
                WHERE date = '{date:yyyy-MM-dd}'
                  AND close > 0
                  AND volume > 0
                  AND dollar_volume_rank <= {topN}
                ORDER BY dollar_volume_rank
                FORMAT JSONEachRow";

            return ExecuteQuery(query);
        }

        /// <summary>
        /// Test ClickHouse connection
        /// </summary>
        private bool TestConnection()
        {
            try
            {
                var query = "SELECT 1 FORMAT JSONEachRow";
                var results = ExecuteQuery(query);
                return results.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Execute a SQL query against ClickHouse
        /// </summary>
        private List<Dictionary<string, object>> ExecuteQuery(string query)
        {
            var url = $"http://{_host}:{_port}/";
            var content = new StringContent(query, Encoding.UTF8, "application/octet-stream");

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = content;
            request.Headers.Add("X-ClickHouse-Database", _database);
            request.Headers.Add("X-ClickHouse-User", _username);
            if (!string.IsNullOrEmpty(_password))
            {
                request.Headers.Add("X-ClickHouse-Key", _password);
            }

            try
            {
                var response = _httpClient.SendAsync(request).Result;
                response.EnsureSuccessStatusCode();

                var responseBody = response.Content.ReadAsStringAsync().Result;
                return ParseJsonEachRow(responseBody);
            }
            catch (Exception ex)
            {
                Log.Error($"ClickHouse query failed: {ex.Message}");
                return new List<Dictionary<string, object>>();
            }
        }

        /// <summary>
        /// Parse ClickHouse JSONEachRow format
        /// </summary>
        private List<Dictionary<string, object>> ParseJsonEachRow(string json)
        {
            var results = new List<Dictionary<string, object>>();
            var lines = json.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(line);
                    results.Add(obj);
                }
                catch
                {
                    // Skip malformed lines
                }
            }

            return results;
        }
    }
}
