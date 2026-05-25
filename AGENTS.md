# AGENTS.md

## Scope

This file applies to everything under `Lean/`.

## Project Purpose

- This repo is a customized LEAN engine fork used for local and CI backtesting.
- It includes custom ClickHouse integration and a local Docker image workflow.
- Git remotes, **`master` vs `vera/master`**, and VeraLab / strategy-repo image integration: **`VERA_CUSTOMIZATIONS.md`**.

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

### Foundation image — Pydantic (VeraLab JSON)

`Dockerfile` uses **`FROM quantconnect/lean:foundation`** (floating tag). The upstream image ships **Pydantic v2** for Python algorithms; the strategy repo’s **`VeraLab`** model (`vera_lab` package) expects **`pydantic>=2.7,<3`** (no separate `pip install` in our Dockerfile).

| Check | Command / note |
|-------|----------------|
| Quick check | `docker run --rm quantconnect/lean:foundation python3 -c "import pydantic; print(pydantic.__version__)"` — expect **2.x** |
| Multi-arch | Pull/run on **linux/arm64** and **linux/amd64** before relying on a new foundation release (GitHub-hosted CI is usually amd64). |
| If Pydantic missing or wrong major | Restore a `RUN pip install --no-cache-dir "pydantic>=2.7,<3"` line in `Dockerfile` and update this table. |

**Recorded verification (workspace, not a guarantee for every future digest):**

- **2026-04-11** — `quantconnect/lean:foundation` digest `sha256:6e4fd9b135b8d5c10bad3f8ccde5c54c4d4252a5772963e185a0076f78cb58e8`, **linux/arm64**: Pydantic **2.12.5**.

## Git Workflow Notes

- **`master`** mirrors **QuantConnect** (`quantconnect/master` remote); keep it free of fork-only commits.
- All fork work lives on **`vera/master`** (and short-lived branches from it). See **`VERA_CUSTOMIZATIONS.md`** for fetch/rebase/push flow.
- Do not rewrite history or force-push unless explicitly requested.

## Definition of Done

- Build/runtime validations pass for the modified area.
- Provider-separation architecture remains intact.
- Any contract-level change is documented in nearby markdown.
