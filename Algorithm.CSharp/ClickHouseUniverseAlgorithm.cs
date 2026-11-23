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

using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// ClickHouse universe algorithm that works on both local LEAN (with ClickHouse) 
    /// and QuantConnect Cloud (with LEAN's data sources).
    /// 
    /// The universe automatically:
    /// 1. Detects if ClickHouse is available
    /// 2. Pre-fetches all universe data during Initialize() for fast time-slice lookups
    /// 3. Falls back to LEAN data sources if ClickHouse unavailable
    /// 
    /// Configuration (config.json for local LEAN):
    /// {
    ///   "clickhouse-host": "localhost",
    ///   "clickhouse-port": 8123,
    ///   "clickhouse-database": "vera",
    ///   "clickhouse-username": "default",
    ///   "clickhouse-password": ""
    /// }
    /// </summary>
    public class ClickHouseUniverseAlgorithm : QCAlgorithm
    {
        public override void Initialize()
        {
            SetStartDate(2021, 1, 1);
            SetEndDate(2024, 12, 31);
            SetCash(100000);

            UniverseSettings.Resolution = Resolution.Daily;

            // Use ClickHouse sector universe with intelligent caching
            // Works on both:
            // - Local LEAN with ClickHouse (fast SQL queries with caching)
            // - QuantConnect Cloud (falls back to CoarseFundamental)
            AddUniverse(new ClickHouseSectorUniverseSelectionModel(
                algorithm: this,
                sector: "Technology",
                topN: 20
            ));

            Log($"ClickHouseUniverseAlgorithm: Initialized");
            Log($"  - Start Date: {StartDate:yyyy-MM-dd}");
            Log($"  - End Date: {EndDate:yyyy-MM-dd}");
            Log($"  - ClickHouse will be auto-detected and cached");

            // Monthly rebalancing
            Schedule.On(
                DateRules.MonthStart(),
                TimeRules.At(9, 30),
                Rebalance
            );
        }

        private void Rebalance()
        {
            var symbols = UniverseManager.ActiveSecurities.Keys.ToList();
            
            if (symbols.Count == 0)
            {
                Log("Rebalance: No symbols in universe");
                return;
            }

            Log($"Rebalance: {symbols.Count} symbols, rebalancing to equal weight");

            var targetWeight = 1.0m / symbols.Count;
            
            foreach (var symbol in symbols)
            {
                SetHoldings(symbol, targetWeight);
            }
        }

        public override void OnData(Slice data)
        {
            // Intraday logic here if needed
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                Log($"Order filled: {orderEvent.Symbol} {orderEvent.Direction} {orderEvent.FillQuantity} @ {orderEvent.FillPrice:C}");
            }
        }
    }
}
