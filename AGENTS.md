# AGENTS.md

## Scope

This file applies to everything under `Lean/`.

## Project Purpose

- This repo is a customized LEAN engine fork used for local and CI backtesting.
- It includes custom ClickHouse integration and a local Docker image workflow.

## Critical Architecture Rules

- Keep provider responsibilities separate:
  - `Engine/DataFeeds/ClickHouseDataProvider.cs` handles on-demand equity daily zip materialization/freshness.
  - `Common/Data/UniverseSelection/ClickHouseUniverseSelectionProvider` handles ticker screening/universe inputs.
- Do not merge universe-selection logic into the engine data-feed provider.
- Do not bypass LEAN data pipeline unless explicitly requested.

## Modified-from-Upstream File References

- Keep this section updated whenever files diverge from upstream/original LEAN behavior.
- Paths below are relative to the `Lean/` project root.
- Current known modified paths:
  - `Engine/DataFeeds/ClickHouseDataProvider.cs`
  - `Common/Data/UniverseSelection/ClickHouseDataProvider.cs`
  - `Common/Data/UniverseSelection/ClickHouseDollarVolumeUniverseSelectionModel.cs`
  - `Common/Data/UniverseSelection/ClickHouseSectorUniverseSelectionModel.cs`
  - `Common/Data/UniverseSelection/ClickHouseIndustryUniverseSelectionModel.cs`
  - `Algorithm.CSharp/ClickHouseUniverseAlgorithm.cs`
  - `Dockerfile`
  - `Common/Data/UniverseSelection/ClickHouseUniverseSelectionIntent.md`
  - `Engine/DataFeeds/ClickHouseDataProvider.md`

## Build and Run (Local)

- Build release artifacts in containerized .NET 10 environment when host SDK is behind:
  - `docker run --rm -v /Users/mdiggory/FinanceProjects/Lean:/Lean -w /Lean mcr.microsoft.com/dotnet/sdk:10.0 dotnet build -c Release`
- Build image:
  - `docker build --tag lean-local:latest --tag lean-local:$(git rev-parse --short HEAD) -f Dockerfile .`
- Lean CLI uses `/Lean/Launcher/bin/Debug/config.json` mount path; keep Dockerfile Debug/Release compatibility intact.

## Coding Expectations

- Keep changes small and surgical; preserve upstream LEAN style and naming.
- Prefer compatibility-safe changes over broad refactors.
- Avoid hardcoding secrets/host credentials in code.
- When changing data provider behavior, include a short intent note in nearby markdown if behavior is non-obvious.

## Do Not

- Do not collapse universe-selection responsibilities into engine data-feed provider code.
- Do not change public behavior across unrelated LEAN modules in one patch.
- Do not commit experimental toggles or temporary debug instrumentation.

## Validation Expectations

- At minimum, validate with a build that covers modified projects.
- If touching Docker/runtime behavior, run a local backtest through `lean` using the custom image.

### Verification Matrix

1. Build affected projects.
2. Build Docker image (`lean-local:latest`) if runtime/binary files changed.
3. Run one smoke backtest via `lean` using the target image.

## Git Workflow Notes

- Treat upstream mirror branch as clean and keep custom changes on `custom/*` or feature branches.
- Do not rewrite history or force-push unless explicitly requested.

## Definition of Done

- Build/runtime validations pass for the modified area.
- Provider-separation architecture remains intact.
- Any contract-level change is documented in nearby markdown.
