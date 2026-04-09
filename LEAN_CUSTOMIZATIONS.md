# LEAN Customizations and Git Workflow

## Overview

This project uses a customized version of QuantConnect LEAN with specific fixes and enhancements. We maintain a clean separation between upstream LEAN code and our customizations to make updates manageable.

## LEAN Repository Structure

**Location**: `/Users/mdiggory/FinanceProjects/Lean`

**Remotes**:
- `origin` → https://github.com/QuantConnect/Lean.git (upstream)
- `myprivate` → https://github.com/mdiggory/Lean.git (your fork)

**Branches**:
- `master` - Should mirror upstream (currently has customizations - needs cleanup)
- `custom/main` - Production branch with all customizations
- `custom/python-gil-fix` - Python GIL shutdown fix
- `custom/*` - Other customization branches

## Current Customizations

### 1. Python GIL Shutdown Fix ✅

**Branch**: `custom/python-gil-fix`  
**File**: `Common/Python/PythonInitializer.cs`  
**Status**: Active in production

**Problem**: 
- LEAN containers would hang for hours after backtest completion
- Python.NET's `PythonEngine.Shutdown()` caused GIL finalization errors
- Error: "GIL must always be released, and it must be released from the same thread that acquired it"

**Solution**:
```csharp
// Skip PythonEngine.Shutdown() to avoid GIL finalization errors
// The process is exiting anyway, so Python cleanup will happen automatically
Log.Trace("PythonInitializer.Shutdown(): Skipping PythonEngine.Shutdown() to prevent GIL errors");
```

**Impact**:
- Containers now exit cleanly in ~1 minute instead of hanging indefinitely
- No functional impact on backtest results
- Slight memory leak on shutdown (irrelevant since process exits)

**Upstream Potential**: Medium - This is a real bug that affects Python algorithm users

### 2. ARM64 Build Script

**Branch**: `custom/main`  
**File**: `build-docker-local`  
**Status**: Active in production

**Purpose**: Build LEAN Docker image optimized for Apple Silicon (M1/M2/M3)

**Features**:
- Simplified build process for local development
- Tags images with git commit hash
- ARM64-specific optimizations

**Upstream Potential**: Low - Too specific to local development workflow

## Docker Image Strategy

### Production Image
```bash
# Built from custom/main branch
cd /Users/mdiggory/FinanceProjects/Lean
git checkout custom/main
./build-docker-local
# Creates: lean-local:latest
```

### Testing Upstream Updates
```bash
# Build from clean upstream
git checkout master
git pull origin master
./build-docker-local
# Creates: lean-local:upstream (tag manually)
```

## Updating from Upstream LEAN

### Step 1: Update Master Branch
```bash
cd /Users/mdiggory/FinanceProjects/Lean
git checkout master
git fetch origin
git reset --hard origin/master  # Clean slate
git push myprivate master --force
```

### Step 2: Rebase Customizations
```bash
# Rebase Python GIL fix
git checkout custom/python-gil-fix
git rebase master

# Test the fix still works
./build-docker-local
# Run test backtest

# If successful, push
git push myprivate custom/python-gil-fix --force-with-lease
```

### Step 3: Update Production Branch
```bash
git checkout custom/main
git rebase master

# Or merge if you prefer
# git merge master --no-ff

# Rebuild production image
./build-docker-local

# Test with VeraTradingLab workflows
cd /Users/mdiggory/FinanceProjects/VeraTradingLab
gh workflow run backtest.yml -f project=simple_test

# If successful, push
git push myprivate custom/main --force-with-lease
```

## Workflow Integration

### VeraTradingLab Workflows

The GitHub Actions workflows in VeraTradingLab use the local LEAN Docker image:

**File**: `.github/workflows/backtest.yml`
```yaml
runs-on: self-hosted  # Mac runner
# ...
docker run --rm lean-local:latest  # Uses custom/main build
```

**Dependencies**:
1. LEAN repo at `/Users/mdiggory/FinanceProjects/Lean`
2. `custom/main` branch checked out
3. `lean-local:latest` Docker image built from `custom/main`

### Rebuilding After LEAN Updates

```bash
# After updating LEAN customizations
cd /Users/mdiggory/FinanceProjects/Lean
git checkout custom/main
./build-docker-local

# Verify in VeraTradingLab
cd /Users/mdiggory/FinanceProjects/VeraTradingLab
gh workflow run backtest.yml -f project=simple_test
```

## Contributing Back to Upstream

If a customization is generally useful (like the GIL fix):

```bash
cd /Users/mdiggory/FinanceProjects/Lean

# Create clean branch from upstream
git checkout master
git pull origin master
git checkout -b bugfix/python-gil-shutdown

# Cherry-pick only the GIL fix commit
git log custom/python-gil-fix --oneline
git cherry-pick <commit-hash>

# Push to your fork
git push myprivate bugfix/python-gil-shutdown

# Create PR on GitHub:
# https://github.com/QuantConnect/Lean/compare/master...mdiggory:bugfix/python-gil-shutdown
```

## Troubleshooting

### "Container hangs after backtest"
- Check you're using `lean-local:latest` built from `custom/main`
- Verify GIL fix is present: `git log custom/main --grep="GIL"`
- Rebuild image: `cd Lean && ./build-docker-local`

### "Merge conflicts when updating from upstream"
- Check which files conflict: `git status`
- If `PythonInitializer.cs` conflicts, carefully preserve the GIL fix
- Test thoroughly after resolving conflicts

### "Workflow uses wrong LEAN version"
- Check Docker image: `docker images | grep lean-local`
- Verify build date matches recent rebuild
- Force rebuild: `docker rmi lean-local:latest && ./build-docker-local`

## Best Practices

1. **Never commit directly to `master`** - Keep it clean for upstream updates
2. **Always work in `custom/*` branches** - Easy to track what's customized
3. **Test after rebasing** - Upstream changes might break customizations
4. **Document customizations** - Update this file when adding new ones
5. **Tag Docker images** - Know which customizations are in production
6. **Keep customizations minimal** - Easier to maintain and merge

## Quick Reference

```bash
# See all customizations
cd /Users/mdiggory/FinanceProjects/Lean
git diff master...custom/main

# List custom branches
git branch --list 'custom/*'

# Check current LEAN branch
git branch --show-current

# Rebuild production LEAN image
git checkout custom/main && ./build-docker-local

# Test LEAN changes
cd /Users/mdiggory/FinanceProjects/VeraTradingLab
gh workflow run backtest.yml -f project=simple_test
```

## Related Documentation

- **LEAN Git Workflow**: `/Users/mdiggory/FinanceProjects/Lean/GIT_WORKFLOW.md`
- **Oracle Cloud Setup**: `docs/ORACLE_CLOUD_QUICK_START.md`
- **LEAN Optimization Strategy**: `LEAN_OPTIMIZATION_STRATEGY.md`

## Maintenance Schedule

- **Weekly**: Check for upstream LEAN updates
- **Monthly**: Rebase customizations on latest upstream
- **Quarterly**: Review if customizations can be contributed upstream
- **As needed**: Update when LEAN releases major versions
