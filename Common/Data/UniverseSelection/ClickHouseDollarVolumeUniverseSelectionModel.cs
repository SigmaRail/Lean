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
    /// Dollar volume based universe selection using ClickHouse data.
    /// 
    /// Selects top N stocks by dollar volume, similar to CoarseFundamentalUniverse
    /// but queries ClickHouse directly for faster performance.
    /// </summary>
    public class ClickHouseDollarVolumeUniverseSelectionModel : Universe
    {
        private readonly int _topN;
        private readonly ClickHouseDataProvider _provider;

        /// <summary>
        /// Initializes a new instance of the ClickHouseDollarVolumeUniverseSelectionModel
        /// </summary>
        /// <param name="topN">Number of top stocks by dollar volume to select (default: 100)</param>
        /// <param name="universeSettings">Optional universe settings</param>
        public ClickHouseDollarVolumeUniverseSelectionModel(
            int topN = 100,
            UniverseSettings universeSettings = null)
            : base(CreateConfiguration(FundamentalUniverseFactory.SymbolFactory.UniverseSymbol()))
        {
            _topN = topN;
            UniverseSettings = universeSettings ?? new UniverseSettings(Resolution.Daily, 1, false, false, TimeSpan.FromDays(1));
            
            // Initialize ClickHouse provider with default settings
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
            
            // Query ClickHouse for dollar volume universe
            var results = _provider.GetDollarVolumeUniverse(tradingDate, _topN);
            
            if (results == null || results.Count == 0)
            {
                Log.Debug($"ClickHouseDollarVolumeUniverseSelectionModel: No data for {tradingDate:yyyy-MM-dd}");
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

            Log.Debug($"ClickHouseDollarVolumeUniverseSelectionModel: Selected {symbols.Count} symbols for {tradingDate:yyyy-MM-dd}");
            
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
