# ClickHouse Universe Selection Intent

## Purpose

This folder contains the **universe screening** layer for LEAN integration with ClickHouse.

These classes are responsible for:

- Querying ClickHouse for fundamental and liquidity fields
- Filtering the available tickers by sector, industry, or dollar volume
- Returning the symbols that should enter the universe
- Caching the universe rows by trading date for fast lookups during backtests

## Important separation of concerns

These classes are **not** responsible for bar materialization or zip-file generation.

That job belongs to:

- `Lean/Engine/DataFeeds/ClickHouseDataProvider.cs`

## Intended flow

1. The universe provider queries `daily_screener` for the current trading date.
2. It selects the candidate tickers using fundamental criteria.
3. LEAN then requests bar data for those symbols through the engine data-feed layer.
4. The engine-side ClickHouse provider can materialize the daily equity zip files on demand.

## Why this exists

The goal is to keep universe selection simple, explicit, and easy to reason about:

- screening first
- bar loading second
- no mixing of responsibilities between the two systems

## Related classes

- `ClickHouseUniverseSelectionProvider`
- `ClickHouseDollarVolumeUniverseSelectionModel`
- `ClickHouseSectorUniverseSelectionModel`
- `ClickHouseIndustryUniverseSelectionModel`
