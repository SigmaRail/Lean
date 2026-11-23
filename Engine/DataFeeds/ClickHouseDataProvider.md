# ClickHouse Bar Provider Intent

## Purpose

This provider is the **bar** layer for LEAN integration with ClickHouse.

LEAN's daily equity feed requires a zip-shaped stream (`ticker.csv` inside
`ticker.zip`). That zip is a **transport format**. ClickHouse emits the CSV
lines; C# wraps them as a one-entry zip for `ZipDataCacheProvider`.

## Important separation of concerns

This provider should **not** be used for universe screening.

That job belongs to the universe-selection classes under:

- `Lean/Common/Data/UniverseSelection/`

Bars are read from **`daily_metrics`** (OHLCV). Do not hydrate from L7
`screener_price_tech_daily` — that table is a wide ReplacingMergeTree with
`FINAL` cost. Screening stays on L7 via `/v1/universe/run`.

## Intended flow

1. Python `prehydrate_tickers` calls `PreHydrate(all unique names)` **before** `AddEquity`.
2. `PreHydrate` runs large (`2000` tickers) ClickHouse queries against
   `daily_metrics` (no `FINAL`):
   `ticker IN (...) AND date BETWEEN backtest_start-pad AND backtest_end`.
   Screen-span `start`/`end` arguments are **ignored**.
3. Rows are split by ticker and stored as full-range zip envelopes in RAM.
4. Lean `Fetch("equity/usa/daily/{ticker}.zip")` is a **cache hit**. Misses fall
   back to one single-ticker full-range query and are stored.
5. `Evict` is a no-op so a still-invested name cannot lose bars.

Empty CH results return `null` from `Fetch` so `DefaultDataProvider` can try disk.

## Why this exists

- Bulk load (not one query per ticker per day, not 50-name overlapping scans)
- Full-range envelopes so a 10-year hold still has bars
- TabSeparated from ClickHouse (no JSON bar path)
- no universe filtering logic mixed into this provider

## Related class

- `ClickHouseDataProvider`
