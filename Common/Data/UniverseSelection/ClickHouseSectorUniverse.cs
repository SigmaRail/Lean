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
using QuantConnect.Interfaces;

namespace QuantConnect.Data.UniverseSelection
{
    /// <summary>
    /// Sector universe selection using ClickHouse with intelligent caching.
    /// 
    /// Pre-fetches all universe data during Initialize() for O(1) time slice lookups.
    /// Works on both local LEAN (with ClickHouse) and QuantConnect Cloud (fallback to LEAN data).
    /// </summary>
    public class ClickHouseSectorUniverse : Universe
    {
        private readonly string _sector;
        private readonly int _topN;
        private readonly ClickHouseUniverseProvider _provider;
        private readonly IAlgorithm _algorithm;

        /// <summary>
        /// Initializes a new instance of the ClickHouseSectorUniverse
        /// </summary>
        /// <param name="algorithm">The algorithm instance (for date range and fallback)</param>
        /// <param name="sector">Sector name (e.g., "Technology", "Healthcare")</param>
        /// <param name="topN">Number of top stocks to select (default: 20)</param>
        /// <param name="universeSettings">Optional universe settings</param>
        public ClickHouseSectorUniverse(
            IAlgorithm algorithm,
            string sector, 
            int topN = 20,
            UniverseSettings universeSettings = null)
            : base(CreateConfiguration(FundamentalUniverseFactory.SymbolFactory.UniverseSymbol()))
        {
            _algorithm = algorithm;
            _sector = sector;
            _topN = topN;
            UniverseSettings = universeSettings ?? new UniverseSettings(Resolution.Daily, 1, false, false, TimeSpan.FromDays(1));
            
            // Initialize provider with caching (auto-detects ClickHouse availability)
            _provider = new ClickHouseUniverseProvider(algorithm);
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
            
            // Try ClickHouse first
            if (_provider.IsClickHouseAvailable)
            {
                var results = _provider.GetSectorUniverse(tradingDate, _sector, _topN);
                
                if (results != null && results.Count > 0)
                {
                    var symbols = results.Select(row => 
                    {
                        var ticker = row.ContainsKey("ticker") ? row["ticker"].ToString() : null;
                        if (string.IsNullOrEmpty(ticker)) return null;
                        return Symbol.Create(ticker, SecurityType.Equity, Market.USA);
                    }).Where(s => s != null).ToList();

                    Log.Debug($"ClickHouseSectorUniverse: Selected {symbols.Count} symbols from {_sector} sector for {tradingDate:yyyy-MM-dd}");
                    return symbols;
                }
            }
            
            // Fallback to LEAN's CoarseFundamental data (for QuantConnect Cloud)
            return SelectFromCoarseFundamental(data.Data.OfType<CoarseFundamental>());
        }

        /// <summary>
        /// Fallback selection using CoarseFundamental data (works on QuantConnect Cloud)
        /// </summary>
        private IEnumerable<Symbol> SelectFromCoarseFundamental(IEnumerable<CoarseFundamental> coarseData)
        {
            // Note: CoarseFundamental doesn't have sector info, so this is a limitation
            // We select by dollar volume as a proxy
            Log.Debug("ClickHouseDualModeSectorUniverse: Falling back to CoarseFundamental (sector filtering not available)");
            
            return coarseData
                .OrderByDescending(c => c.DollarVolume)
                .Take(_topN)
                .Select(c => c.Symbol);
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
