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
using QuantConnect.Interfaces;
using QuantConnect.Logging;

namespace QuantConnect.Data.UniverseSelection
{
    /// <summary>
    /// Sector-based universe selection using ClickHouse data with intelligent caching.
    /// 
    /// Selects top N stocks from a specific sector ordered by dollar volume.
    /// Pre-fetches all data for the algorithm's date range during initialization
    /// for O(1) time-slice lookups.
    /// </summary>
    public class ClickHouseSectorUniverseSelectionModel : Universe
    {
        private readonly string _sector;
        private readonly int _topN;
        private readonly ClickHouseUniverseSelectionProvider _provider;

        /// <summary>
        /// Initializes a new instance of the ClickHouseSectorUniverseSelectionModel with caching
        /// </summary>
        /// <param name="algorithm">The algorithm instance to get StartDate/EndDate for caching</param>
        /// <param name="sector">Sector name (e.g., "Technology", "Healthcare")</param>
        /// <param name="topN">Number of top stocks to select (default: 20)</param>
        /// <param name="universeSettings">Optional universe settings</param>
        public ClickHouseSectorUniverseSelectionModel(
            IAlgorithm algorithm,
            string sector, 
            int topN = 20,
            UniverseSettings universeSettings = null)
            : base(CreateConfiguration(FundamentalUniverseFactory.SymbolFactory.UniverseSymbol()))
        {
            _sector = sector;
            _topN = topN;
            UniverseSettings = universeSettings ?? new UniverseSettings(Resolution.Daily, 1, false, false, TimeSpan.FromDays(1));
            
            // Initialize ClickHouse provider with algorithm for caching
            _provider = new ClickHouseUniverseSelectionProvider(algorithm);
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
            
            // Query ClickHouse for sector universe
            var results = _provider.GetSectorUniverse(tradingDate, _sector, _topN);
            
            if (results == null || results.Count == 0)
            {
                Log.Debug($"ClickHouseSectorUniverseSelectionModel: No data for {tradingDate:yyyy-MM-dd} sector {_sector}");
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
                
                return Symbol.Create(ticker, SecurityType.Equity, QuantConnect.Market.USA);
            }).Where(s => s != null).ToList();

            Log.Debug($"ClickHouseSectorUniverseSelectionModel: Selected {symbols.Count} symbols from {_sector} sector for {tradingDate:yyyy-MM-dd}");
            
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
