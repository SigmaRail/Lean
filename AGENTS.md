# SigmaRail Lean contributor guidance

## Purpose and architecture

This repository is SigmaRail's customized LEAN engine integration. Read the
[three-project architecture](../sigmarail-spec/docs/ARCHITECTURE.md) before changing strategy,
universe, data-feed, or runtime behavior.

- **SigmaRail Spec** defines the versioned strategy, AST, slot, and capability contracts.
- **SigmaRail** owns ClickHouse schemas, transforms, data loading, queries, and the research interface.
- **SigmaRail Lean** implements the LEAN runtime integration and executes supported contracts.
- The engine data-feed provider and universe-selection provider have separate responsibilities.
  Do not move screening logic into the data-feed provider or bypass LEAN's data pipeline.
- Keep the five slots and their contract-defined responsibilities intact. Do not invent a new slot or
  silently change how a slot interprets a document.

## Runtime boundaries

- `Engine/DataFeeds/ClickHouseDataProvider.cs` bulk-loads daily equity bars from ClickHouse and serves
  them through LEAN's data-feed path. It does not decide universe membership.
- `Common/Data/UniverseSelection/ClickHouseUniverseSelectionProvider` supplies universe inputs.
- ClickHouse credentials and database selection come from runtime configuration. Never commit them.
- Strategy JSON and parameters must conform to the published SigmaRail Spec contract. If a behavior
  cannot be expressed there, define and version the contract before implementing it here.

## Modified-from-upstream files

Keep this list current when changing LEAN behavior:

- `Engine/DataFeeds/ClickHouseDataProvider.cs`
- `Common/Data/UniverseSelection/ClickHouseDataProvider.cs`
- `Common/Data/UniverseSelection/ClickHouseDollarVolumeUniverseSelectionModel.cs`
- `Common/Data/UniverseSelection/ClickHouseSectorUniverseSelectionModel.cs`
- `Common/Data/UniverseSelection/ClickHouseIndustryUniverseSelectionModel.cs`
- `Algorithm.CSharp/ClickHouseUniverseAlgorithm.cs`
- `Dockerfile`
- `Common/Data/UniverseSelection/ClickHouseUniverseSelectionIntent.md`
- `Engine/DataFeeds/ClickHouseDataProvider.md`

## Build and run locally

Build release artifacts with the containerized .NET 10 SDK when the host SDK is behind:

```bash
docker run --rm -v "$PWD":/Lean -w /Lean mcr.microsoft.com/dotnet/sdk:10.0 dotnet build -c Release
docker build --tag lean-local:latest -f Dockerfile .
```

LEAN CLI mounts configuration at `/Lean/Launcher/bin/Debug/config.json`. Keep the Dockerfile's
Debug/Release path compatibility intact.

The foundation image currently supplies Pydantic v2 for Python strategy configuration. Re-check the
image when its tag or digest changes. Test both `linux/arm64` and `linux/amd64` before relying on a
new foundation image across local and hosted runners.

## Contribution and validation rules

- Follow the repository's configured upstream and SigmaRail contribution branches; verify their
  current names before branch operations.
- Use the primary checkout when the user directs it. Do not create a worktree unless the user asks.
- Keep changes focused and compatible with upstream LEAN conventions.
- After changing provider behavior, update the nearby intent note when the behavior is not obvious.
- Validate affected projects with a Release build. Rebuild the Docker image after runtime changes and
  run a smoke backtest through LEAN when the runtime or container changes.
- Do not rewrite history, force-push, publish an image, or change a hosted runtime unless explicitly
  authorized.

See [SigmaRail customizations](SIGMARAIL_CUSTOMIZATIONS.md) for the fork's integration points.
