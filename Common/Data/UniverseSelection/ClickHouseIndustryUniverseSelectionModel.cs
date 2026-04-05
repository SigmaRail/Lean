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
using Python.Runtime;

namespace QuantConnect.Data.UniverseSelection
{
    /// <summary>
    /// Industry-based universe selection using ClickHouse data.
    /// 
    /// Selects top N stocks from a specific industry ordered by dollar volume.
    /// More granular than sector selection for targeted industry-specific strategies.
    /// Queries ClickHouse database directly via HTTP for fast universe selection.
    /// </summary>
    public class ClickHouseIndustryUniverseSelectionModel : Universe
    {
        private readonly string _industry;
        private readonly int _topN;
        private readonly ClickHouseDataProvider _provider;

        /// <summary>
        /// Initializes a new instance of the ClickHouseIndustryUniverseSelectionModel
        /// </summary>
        /// <param name="industry">Industry name (e.g., "Software - Application", "Semiconductors")</param>
        /// <param name="topN">Number of top stocks to select (default: 10)</param>
        /// <param name="universeSettings">Optional universe settings</param>
        public ClickHouseIndustryUniverseSelectionModel(
            string industry, 
            int topN = 10,
            UniverseSettings universeSettings = null)
            : base(CreateConfiguration(FundamentalUniverseFactory.SymbolFactory.UniverseSymbol()))
        {
            _industry = industry;
            _topN = topN;
            UniverseSettings = universeSettings ?? new UniverseSettings(Resolution.Daily, 1, false, false, TimeSpan.FromDays(1));
            
            // Initialize ClickHouse provider with default settings
            // Override these via environment variables or configuration
            var host = Config.Get("clickhouse-host", "localhost");
            var port = Config.GetInt("clickhouse-port", 8123);
            var database = Config.Get("clickhouse-database", "vera");
            var username = Config.Get("clickhouse-username", "default");
            var password = Config.Get("clickhouse-password", "");
            
            _provider = new ClickHouseDataProvider(host, port, database, username, password);
        }

        /// <summary>
        /// Performs universe selection using the data specified
        /// </summary>
        /// <param name="utcTime">The current utc time</param>
        /// <param name="data">The symbols to remain in the universe</param>
        /// <returns>The data that passes the filter</returns>
        public override IEnumerable<Symbol> SelectSymbols(DateTime utcTime, BaseDataCollection data)
        {
            // Convert UTC to US Eastern Time for trading date
            var etTime = utcTime.ConvertFromUtc(TimeZones.NewYork);
            var tradingDate = etTime.Date;
            
            // Query ClickHouse for industry universe
            var results = _provider.GetIndustryUniverse(tradingDate, _industry, _topN);
            
            if (results == null || results.Count == 0)
            {
                Log.Debug($"ClickHouseIndustryUniverseSelectionModel: No data for {tradingDate:yyyy-MM-dd} industry {_industry}");
                return Enumerable.Empty<Symbol>();
            }

            // Convert to LEAN Symbol objects
            var symbols = results.Select(row => 
            {
                var ticker = row.ContainsKey("ticker") ? row["ticker"].ToString() : null;
                if (string.IsNullOrEmpty(ticker))
                {
                    return null;
                }
                
                return Symbol.Create(ticker, SecurityType.Equity, Market.USA);
            }).Where(s => s != null).ToList();

            Log.Debug($"ClickHouseIndustryUniverseSelectionModel: Selected {symbols.Count} symbols from {_industry} industry for {tradingDate:yyyy-MM-dd}");
            
            return symbols;
        }

        /// <summary>
        /// Creates a subscription configuration for this universe
        /// </summary>
        private static SubscriptionDataConfig CreateConfiguration(Symbol symbol)
        {
            return FundamentalUniverseFactory.CreateConfiguration(symbol);
        }
    }
}
