# SigmaRail fork: customizations and git workflow

This fork adds ClickHouse-backed data and universe selection, a Python.NET shutdown workaround, launcher wiring, and the public multi-platform LEAN image used by SigmaRail. Architectural rules and file-level pointers live in `AGENTS.md`; this document covers branch layout, upstream synchronization, and image releases.

## Repository layout

Typical clone (paths vary):

- **Fork (your remote):** `https://github.com/SigmaRail/Lean.git` → git remote name **`origin`**
- **Upstream QuantConnect:** `https://github.com/QuantConnect/Lean.git` → git remote name **`quantconnect`** (add if missing: `git remote add quantconnect https://github.com/QuantConnect/Lean.git`)

### Branches

| Branch | Purpose |
|--------|---------|
| **`master`** | Tracks **QuantConnect** `master` only. Reset or merge from `quantconnect/master` here; **no SigmaRail custom commits** on this branch. |
| **`SigmaRail/master`** | All **fork customizations** (ClickHouse, Docker, GIL workaround, CI scripts, docs). Rebase (or merge) this branch onto an updated **`master`** when refreshing upstream. |

Feature work can use short-lived branches off **`SigmaRail/master`**; integrate back with PRs or merges into **`SigmaRail/master`**.

### What `SigmaRail/master` contains (summary)

- **`Engine/DataFeeds/ClickHouseDataProvider.cs`** — equity daily zip materialization via ClickHouse HTTP; used with **`CompositeDataProvider`**.
- **`Common/Data/UniverseSelection/ClickHouse*.cs`** — universe screening over ClickHouse (separate from the engine feed provider).
- **`Launcher/Program.cs`** — applies job parameters to `clickhouse-*` config after a job exists; may switch to **`CompositeDataProvider`** when `composite-data-providers` is set (see comment in file re: dotted config keys).
- **`Launcher/config.json`** — default **`CompositeDataProvider`** listing **`DefaultDataProvider`** + **`ClickHouseDataProvider`**.
- **`Common/Python/PythonInitializer.cs`** — skips **`PythonEngine.Shutdown()`** to avoid GIL hang on container exit.
- **`Dockerfile`**, **`build-docker-*`**, **`push-docker-image`**, **`.github/workflows/*`** — image build and optional CI.
- **`Algorithm.CSharp/ClickHouseUniverseAlgorithm.cs`** — reference C# algorithm (optional).

Details and verification steps: **`AGENTS.md`**.

## Docker images

- **Local tag:** build per **`AGENTS.md`** (e.g. `lean-local:latest` and optional commit tag).
- **Published from this repository:** `ghcr.io/sigmarail/lean/lean-clickhouse` uses public multi-platform `latest` and 12-character commit tags.

Rebuild the image after changing **`SigmaRail/master`** engine or provider code; the **`Dockerfile`** expects **pre-built `Release`** binaries copied into the image.

## Updating from QuantConnect

### 1. Refresh `master` from upstream

```bash
git fetch quantconnect
git checkout master
git reset --hard quantconnect/master   # or: merge if you prefer merge commits
git push origin master                  # fork’s master mirrors QC
```

### 2. Rebase SigmaRail work on top

```bash
git checkout SigmaRail/master
git fetch origin
git rebase master                        # resolve conflicts; prefer keeping SigmaRail semantics in Program.cs / config / providers
```

Validate: **Release build**, **Docker build**, one **Lean CLI backtest** (see **`AGENTS.md`** verification matrix).

### 3. Publish the branch

```bash
git push origin SigmaRail/master --force-with-lease
```

Use **`--force-with-lease`** only when history was rewritten (e.g. after rebase).

### High-conflict files on upstream sync

Expect to re-validate after each upstream bump:

- `Launcher/Program.cs`
- `Launcher/config.json`
- `Common/Python/PythonInitializer.cs`
- `Engine/DataFeeds/ClickHouseDataProvider.cs` (if QC touches data-feed plumbing)

## Launcher config vs job parameters

- **Lean CLI / local platform:** ClickHouse connection and backtest window often come from **`config.json`** / **`lean.json`** and project config; see the SigmaRail trading-lab configuration and **`--extra-docker-config`**.
- **Non–local-platform jobs:** **`ApplyJobParametersToConfig`** maps algorithm job parameters into **`clickhouse-*`** keys (with aliases such as **`vera-host`**) so cloud-style job packets can drive the same provider stack.

If you change this mapping, update **`AGENTS.md`** or **`SIGMARAIL_CUSTOMIZATIONS.md`** in the same PR.

## SigmaRail image consumption

- SigmaRail CI pulls **`ghcr.io/sigmarail/lean/lean-clickhouse`** using a commit-specific tag. The legacy **`LeanProjects/VeraTradingLab/`** consumer can continue using the public `latest` tag during migration.
- Self-hosted runners may use **arm64** images; GitHub-hosted **ubuntu** jobs typically use **amd64** **`latest`**.

After changing the fork, rebuild/push the image (your **`build-docker-image`** / **`push-docker-image`** flow) before expecting CI to pick up engine changes.

## Contributing a change upstream (e.g. GIL fix)

To open a **minimal** PR against QuantConnect:

```bash
git fetch quantconnect
git checkout -b bugfix/python-gil-shutdown quantconnect/master
# Cherry-pick or re-apply only the PythonInitializer change from SigmaRail/master
git push origin bugfix/python-gil-shutdown
# Open PR: QuantConnect/Lean ← your fork’s branch
```

ClickHouse-specific code is unlikely to be upstreamed as-is; treat it as fork-only unless QC adopts a similar extension point.

## Troubleshooting

| Symptom | Things to check |
|--------|-------------------|
| Container hangs after Python backtest | GIL workaround present in **`PythonInitializer.cs`**; image built from current **`SigmaRail/master`**. |
| No ClickHouse data | **`composite-data-providers`**, **`clickhouse-host`**, credentials, and backtest date keys; **`SIGMARAIL_*`** + **`CLICKHOUSE_*`** env in container when using the strategy repo. |
| Merge/rebase conflicts | **`git status`**; preserve **`SigmaRail/master`** semantics in launcher and providers, then rebuild. |
| Wrong engine in CI | Image tag in workflow; digest/tag date; rebuild and push **`ghcr.io/sigmarail/lean/lean-clickhouse`**. |

## Quick reference

```bash
git fetch quantconnect && git fetch origin

# See fork changes vs upstream master
git log master..SigmaRail/master --oneline
git diff --stat master...SigmaRail/master

git checkout SigmaRail/master
git rebase master          # after master == quantconnect/master
# build + docker + smoke backtest (see AGENTS.md)

git push origin SigmaRail/master --force-with-lease   # if rebased
```

## Related documentation

- **`AGENTS.md`** (this repo) — architecture, modified paths, build commands, Pydantic/foundation checks.
- **`Engine/DataFeeds/ClickHouseDataProvider.md`**, **`Common/Data/UniverseSelection/ClickHouseUniverseSelectionIntent.md`**
- Workspace branching notes (parent repo): **`LeanResources/GIT_WORKFLOW.md`**
- Strategy runner and image usage: **`LeanProjects/VeraTradingLab/README.md`** and **`docs/GIT_WORKFLOW.md`** inside that repo

## Maintenance

- **After QuantConnect `master` moves:** update local **`master`**, rebase **`SigmaRail/master`**, run verification matrix in **`AGENTS.md`**.
- **When adding a fork-only feature:** extend **`AGENTS.md`** “Modified-from-Upstream” list and add a short bullet in **`SIGMARAIL_CUSTOMIZATIONS.md`** if it affects git or release process.
