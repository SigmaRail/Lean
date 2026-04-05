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
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace QuantConnect.Data.UniverseSelection
{
    /// <summary>
    /// Data provider for querying ClickHouse database directly from LEAN.
    /// Makes HTTP requests to ClickHouse HTTP interface for universe selection.
    /// </summary>
    public class ClickHouseDataProvider
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _database;
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Initializes a new instance of the ClickHouseDataProvider
        /// </summary>
        /// <param name="host">ClickHouse server host (default: localhost)</param>
        /// <param name="port">ClickHouse HTTP port (default: 8123)</param>
        /// <param name="database">Database name (default: vera)</param>
        /// <param name="username">Username (default: default)</param>
        /// <param name="password">Password (optional)</param>
        public ClickHouseDataProvider(
            string host = "localhost",
            int port = 8123,
            string database = "vera",
            string username = "default",
            string password = null)
        {
            _host = host;
            _port = port;
            _database = database;
            _username = username;
            _password = password;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Query ClickHouse for top N stocks by dollar volume on a specific date
        /// </summary>
        /// <param name="date">The date to query</param>
        /// <param name="topN">Number of top stocks to return</param>
        /// <returns>List of dictionaries containing ticker and fundamental data</returns>
        public List<Dictionary<string, object>> GetDollarVolumeUniverse(DateTime date, int topN = 100)
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
        /// Query stocks from a specific sector
        /// </summary>
        /// <param name="date">The date to query</param>
        /// <param name="sector">Sector name</param>
        /// <param name="topN">Number of top stocks to return</param>
        /// <returns>List of dictionaries containing sector stocks</returns>
        public List<Dictionary<string, object>> GetSectorUniverse(DateTime date, string sector, int topN = 20)
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
        /// Query stocks from a specific industry
        /// </summary>
        /// <param name="date">The date to query</param>
        /// <param name="industry">Industry name</param>
        /// <param name="topN">Number of top stocks to return</param>
        /// <returns>List of dictionaries containing industry stocks</returns>
        public List<Dictionary<string, object>> GetIndustryUniverse(DateTime date, string industry, int topN = 10)
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
