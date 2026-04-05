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

using QuantConnect.Data.UniverseSelection;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Example algorithm demonstrating ClickHouse-based universe selection.
    /// 
    /// This algorithm uses the ClickHouse universe models to select stocks
    /// directly from a ClickHouse database, bypassing LEAN's traditional data sources.
    /// 
    /// Configuration required in config.json:
    /// {
    ///   "clickhouse-host": "localhost",
    ///   "clickhouse-port": 8123,
    ///   "clickhouse-database": "vera",
    ///   "clickhouse-username": "default",
    ///   "clickhouse-password": ""
    /// }
    /// </summary>
    public class ClickHouseUniverseExample : QCAlgorithm
    {
        public override void Initialize()
        {
            SetStartDate(2021, 1, 1);
            SetEndDate(2024, 12, 31);
            SetCash(100000);

            // Set universe resolution
            UniverseSettings.Resolution = Resolution.Daily;

            // Example 1: Dollar Volume Universe (Top 100 by dollar volume)
            // AddUniverse(new DollarVolumeUniverseSelectionModel(topN: 100));

            // Example 2: Sector Universe (Top 20 Technology stocks)
            // AddUniverse(new SectorUniverseSelectionModel(sector: "Technology", topN: 20));

            // Example 3: Industry Universe (Top 10 Software stocks)
            AddUniverse(new IndustryUniverseSelectionModel(industry: "Software - Application", topN: 10));

            // Scheduled rebalancing
            Schedule.On(
                DateRules.MonthStart(),
                TimeRules.At(9, 30),
                Rebalance
            );
        }

        private void Rebalance()
        {
            // Get current universe symbols
            var symbols = UniverseManager.ActiveSecurities.Keys.ToList();
            
            if (symbols.Count == 0)
            {
                Debug("No symbols in universe");
                return;
            }

            Debug($"Rebalancing {symbols.Count} symbols: {string.Join(", ", symbols.Take(5))}...");

            // Equal weight allocation
            var targetWeight = 1.0m / symbols.Count;
            
            foreach (var symbol in symbols)
            {
                SetHoldings(symbol, targetWeight);
            }
        }

        public override void OnData(Slice data)
        {
            // Can access data here if needed for intraday logic
        }
    }
}
