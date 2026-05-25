# Vera fork: customizations and git workflow

This fork adds **ClickHouse-backed data and universe selection**, **Docker image build/publish**, a **Python.NET shutdown workaround**, and **launcher wiring** so job parameters and `CompositeDataProvider` work with the Lean CLI. Architectural rules and file-level pointers live in **`AGENTS.md`**; this document focuses on **git layout**, **updating from QuantConnect**, and **how the `VeraTradingLab/` strategy repository consumes the image** (Python algorithm JSON validated as **VeraLab** / `vera_lab`).

## Repository layout

Typical clone (paths vary):

- **Fork (your remote):** `https://github.com/mdiggory/Lean.git` → git remote name **`origin`**
- **Upstream QuantConnect:** `https://github.com/QuantConnect/Lean.git` → git remote name **`quantconnect`** (add if missing: `git remote add quantconnect https://github.com/QuantConnect/Lean.git`)

### Branches

| Branch | Purpose |
|--------|---------|
| **`master`** | Tracks **QuantConnect** `master` only. Reset or merge from `quantconnect/master` here; **no Vera custom commits** on this branch. |
| **`vera/master`** | All **fork customizations** (ClickHouse, Docker, GIL workaround, CI scripts, docs). Rebase (or merge) this branch onto an updated **`master`** when refreshing upstream. |

Feature work can use short-lived branches off **`vera/master`**; integrate back with PRs or merges into **`vera/master`**.

### What `vera/master` contains (summary)

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
- **Published (strategy-repo CI):** `ghcr.io/mdiggory/lean-clickhouse` tags such as **`latest`** (amd64) and **`arm64-latest`** — see **`LeanProjects/VeraTradingLab/`** workflow and README.

Rebuild the image after changing **`vera/master`** engine or provider code; the **`Dockerfile`** expects **pre-built `Release`** binaries copied into the image.

## Updating from QuantConnect

### 1. Refresh `master` from upstream

```bash
git fetch quantconnect
git checkout master
git reset --hard quantconnect/master   # or: merge if you prefer merge commits
git push origin master                  # fork’s master mirrors QC
```

### 2. Rebase Vera work on top

```bash
git checkout vera/master
git fetch origin
git rebase master                        # resolve conflicts; prefer keeping Vera semantics in Program.cs / config / providers
```

Validate: **Release build**, **Docker build**, one **Lean CLI backtest** (see **`AGENTS.md`** verification matrix).

### 3. Publish the branch

```bash
git push origin vera/master --force-with-lease
```

Use **`--force-with-lease`** only when history was rewritten (e.g. after rebase).

### High-conflict files on upstream sync

Expect to re-validate after each upstream bump:

- `Launcher/Program.cs`
- `Launcher/config.json`
- `Common/Python/PythonInitializer.cs`
- `Engine/DataFeeds/ClickHouseDataProvider.cs` (if QC touches data-feed plumbing)

## Launcher config vs job parameters

- **Lean CLI / local platform:** ClickHouse connection and backtest window often come from **`config.json`** / **`lean.json`** and project config; see **`LeanProjects/VeraTradingLab/config.json`** **`docker.environment`** and **`--extra-docker-config`**.
- **Non–local-platform jobs:** **`ApplyJobParametersToConfig`** maps algorithm job parameters into **`clickhouse-*`** keys (with aliases such as **`vera-host`**) so cloud-style job packets can drive the same provider stack.

If you change this mapping, update **`AGENTS.md`** or **`VERA_CUSTOMIZATIONS.md`** in the same PR.

## Strategy repo integration (`LeanProjects/VeraTradingLab/`)

- Workflows under **`LeanProjects/VeraTradingLab/.github/workflows/`** pull **`ghcr.io/mdiggory/lean-clickhouse`** (not a local `lean-local` tag on GitHub-hosted runners).
- Self-hosted runners may use **arm64** images; GitHub-hosted **ubuntu** jobs typically use **amd64** **`latest`**.

After changing the fork, rebuild/push the image (your **`build-docker-image`** / **`push-docker-image`** flow) before expecting CI to pick up engine changes.

## Contributing a change upstream (e.g. GIL fix)

To open a **minimal** PR against QuantConnect:

```bash
git fetch quantconnect
git checkout -b bugfix/python-gil-shutdown quantconnect/master
# Cherry-pick or re-apply only the PythonInitializer change from vera/master
git push origin bugfix/python-gil-shutdown
# Open PR: QuantConnect/Lean ← your fork’s branch
```

ClickHouse-specific code is unlikely to be upstreamed as-is; treat it as fork-only unless QC adopts a similar extension point.

## Troubleshooting

| Symptom | Things to check |
|--------|-------------------|
| Container hangs after Python backtest | GIL workaround present in **`PythonInitializer.cs`**; image built from current **`vera/master`**. |
| No ClickHouse data | **`composite-data-providers`**, **`clickhouse-host`**, credentials, and backtest date keys; **`VERA_LAB_*`** + **`CLICKHOUSE_*`** env in container when using the strategy repo. |
| Merge/rebase conflicts | **`git status`**; preserve **`vera/master`** semantics in launcher and providers, then rebuild. |
| Wrong engine in CI | Image tag in workflow; digest/tag date; rebuild and push **`ghcr.io/mdiggory/lean-clickhouse`**. |

## Quick reference

```bash
git fetch quantconnect && git fetch origin

# See fork changes vs upstream master
git log master..vera/master --oneline
git diff --stat master...vera/master

git checkout vera/master
git rebase master          # after master == quantconnect/master
# build + docker + smoke backtest (see AGENTS.md)

git push origin vera/master --force-with-lease   # if rebased
```

## Related documentation

- **`AGENTS.md`** (this repo) — architecture, modified paths, build commands, Pydantic/foundation checks.
- **`Engine/DataFeeds/ClickHouseDataProvider.md`**, **`Common/Data/UniverseSelection/ClickHouseUniverseSelectionIntent.md`**
- Workspace branching notes (parent repo): **`LeanResources/GIT_WORKFLOW.md`**
- Strategy runner and image usage: **`LeanProjects/VeraTradingLab/README.md`** and **`docs/GIT_WORKFLOW.md`** inside that repo

## Maintenance

- **After QuantConnect `master` moves:** update local **`master`**, rebase **`vera/master`**, run verification matrix in **`AGENTS.md`**.
- **When adding a fork-only feature:** extend **`AGENTS.md`** “Modified-from-Upstream” list and add a short bullet in **`VERA_CUSTOMIZATIONS.md`** if it affects git or release process.
