# LEAN Git Workflow - Managing Customizations

## Repository Structure

```
origin      → https://github.com/QuantConnect/Lean.git (upstream LEAN)
myprivate   → https://github.com/mdiggory/Lean.git (your fork)
```

## Branch Strategy

### Main Branches
- `main` - Clean mirror of upstream QuantConnect/Lean (no customizations)
- `custom/main` - Your production branch with all customizations
- `custom/python-gil-fix` - Specific customization: Python GIL shutdown fix
- `custom/clickhouse` - ClickHouse integration features
- `custom/arm64-optimizations` - ARM64-specific optimizations

### Feature Branches
- `feature/[name]` - New features or experiments
- `bugfix/[name]` - Bug fixes that might be contributed upstream
- `hotfix/[name]` - Critical fixes for production

## Workflow

### 1. Keep `main` Clean (Mirror Upstream)

```bash
# Update main from upstream
git checkout main
git fetch origin
git reset --hard origin/master  # or origin/main
git push myprivate main --force
```

### 2. Maintain Customization Branches

```bash
# Create a new customization branch from main
git checkout main
git checkout -b custom/python-gil-fix

# Make your changes
# Commit with clear messages
git commit -m "fix: Skip PythonEngine.Shutdown() to prevent GIL errors

- Prevents 'GIL must always be released' error during shutdown
- Container now exits cleanly instead of hanging
- Process cleanup happens automatically on exit
"

# Push to your fork
git push myprivate custom/python-gil-fix
```

### 3. Merge Customizations into Production

```bash
# Start from clean main
git checkout main
git pull origin master

# Create/update your production branch
git checkout -b custom/main  # or git checkout custom/main

# Merge customization branches in order
git merge custom/python-gil-fix --no-ff -m "Merge Python GIL fix"
git merge custom/clickhouse --no-ff -m "Merge ClickHouse integration"

# Push to your fork
git push myprivate custom/main
```

### 4. Update from Upstream LEAN

```bash
# Update main from upstream
git checkout main
git fetch origin
git merge origin/master  # or rebase if you prefer

# Rebase your customizations on new main
git checkout custom/python-gil-fix
git rebase main

# Resolve conflicts if any
# Test the customization still works

# Update production branch
git checkout custom/main
git rebase main
# Or merge if you prefer: git merge main

# Push updates
git push myprivate custom/python-gil-fix --force-with-lease
git push myprivate custom/main --force-with-lease
```

## Current Customizations

### 1. Python GIL Shutdown Fix
**Branch**: `custom/python-gil-fix`
**File**: `Common/Python/PythonInitializer.cs`
**Change**: Skip `PythonEngine.Shutdown()` to prevent GIL finalization errors
**Reason**: Prevents container hanging on exit
**Upstream potential**: Could be contributed back to QuantConnect

### 2. ClickHouse Integration (if applicable)
**Branch**: `custom/clickhouse`
**Files**: TBD
**Change**: ClickHouse data provider integration
**Reason**: Custom data source
**Upstream potential**: Unlikely (too specific)

### 3. ARM64 Optimizations (if any)
**Branch**: `custom/arm64-optimizations`
**Files**: Dockerfile, build scripts
**Change**: ARM64-specific build configurations
**Reason**: Apple Silicon compatibility
**Upstream potential**: Maybe (growing ARM adoption)

## Docker Image Tagging Strategy

```bash
# Tag images with branch info
docker build -t lean-local:main .                    # Clean upstream
docker build -t lean-local:custom .                  # With all customizations
docker build -t lean-local:custom-gil-fix .          # Specific customization
docker build -t lean-local:$(git rev-parse --short HEAD) .  # Git hash
```

## Contribution Back to Upstream

If a customization is generally useful:

```bash
# Create a clean branch from upstream main
git checkout main
git checkout -b bugfix/python-gil-shutdown

# Cherry-pick only the relevant commits
git cherry-pick <commit-hash>

# Push to your fork
git push myprivate bugfix/python-gil-shutdown

# Create PR to QuantConnect/Lean from GitHub
```

## Quick Reference

```bash
# See what customizations are in custom/main
git checkout custom/main
git log main..HEAD --oneline

# See diff of all customizations
git diff main...custom/main

# List all custom branches
git branch --list 'custom/*'

# Check which customizations are merged
git branch --merged custom/main | grep custom/
```

## Recovery

If you accidentally commit to `main`:

```bash
# Save your work
git checkout -b custom/accidental-work

# Reset main to upstream
git checkout main
git reset --hard origin/master
git push myprivate main --force

# Merge your work into proper branch
git checkout custom/main
git merge custom/accidental-work
```

## Best Practices

1. **Never commit directly to `main`** - Keep it clean for upstream updates
2. **Use descriptive branch names** - `custom/python-gil-fix` not `fix-bug`
3. **Commit messages matter** - Explain WHY, not just WHAT
4. **Tag your Docker images** - Know which customizations are in each image
5. **Document customizations** - Update this file when adding new ones
6. **Test after rebasing** - Upstream changes might break customizations
7. **Keep customizations minimal** - Easier to maintain and merge
8. **Consider contributing upstream** - Reduces maintenance burden

## Current Status

- ✅ `main` - Currently has customizations (needs cleanup)
- ⚠️  Need to create `custom/*` branches
- ⚠️  Need to reorganize commits

## Next Steps

1. Create `custom/python-gil-fix` branch with GIL fix
2. Reset `main` to clean upstream
3. Create `custom/main` with all customizations
4. Update build scripts to use `custom/main`
5. Document any other customizations
