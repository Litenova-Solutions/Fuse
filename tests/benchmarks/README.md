# Fuse performance benchmarks

This folder documents how to capture wall-clock performance for Fuse pipeline changes. See also [docs/performance.md](../../docs/performance.md) for cold-start methodology and Native AOT packaging.

## Fixture

Use `tests/fixtures/SampleShop` for automated golden-output tests and manual benchmarks. It is a small multi-project .NET solution with planted secrets, a payment dependency cluster, and ASP.NET routes.

For larger benchmarks, choose a repository with a few thousand source files (a medium .NET monorepo or a generated tree).

1. Record the exact command, template, and flags used for every run.
2. Run from a clean shell with no other heavy jobs on the machine.

Example:

```bash
fuse dotnet --directory /path/to/fixture --output /tmp/fuse-bench --overwrite --format xml --tokenizer o200k_base --parallelism 8
```

## Cold start (CLI fixed cost)

One-shot CLI runs pay process startup, DI, and tokenizer init before pipeline work. Measure with a **fresh process**, not a warm shell:

```powershell
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Start-Process -FilePath fuse.exe -ArgumentList "dotnet","--directory","...","--output","...","--overwrite","--format","xml","--tokenizer","o200k_base" -Wait -NoNewWindow
$sw.ElapsedMilliseconds
```

Compare before/after Native AOT builds using the same command. CI smoke tests in `.github/workflows/ci.yml` use this pattern on published AOT binaries.

## Cold run (serial baseline)

Before parallel changes, or to compare serial vs parallel:

```bash
fuse dotnet --directory /path/to/fixture --output /tmp/fuse-bench-cold --overwrite --parallelism 1
```

Record:

- Wall-clock time from the `Stats:` line (duration in seconds).
- Processed file count and token count for sanity checks.

## Parallel run

```bash
fuse dotnet --directory /path/to/fixture --output /tmp/fuse-bench-parallel --overwrite --parallelism %NUMBER_OF_PROCESSORS%
```

On Linux or macOS, replace `%NUMBER_OF_PROCESSORS%` with the output of `nproc` or an explicit core count.

## Warm run (post Phase 3 cache)

After persistent reduction cache is enabled (default unless `--no-cache`):

1. Run once to populate `.fuse/cache/` under the source directory.
2. Run the same command again without `--clear-cache`.
3. Compare the second run wall-clock to the cold parallel run.

## Determinism check

Parallel and serial paths must produce byte-identical fused output for the same inputs. Verify with:

```bash
fuse dotnet --directory /path/to/fixture --output /tmp/fuse-serial --overwrite --parallelism 1
fuse dotnet --directory /path/to/fixture --output /tmp/fuse-parallel --overwrite --parallelism 8
# Compare outputs (hash or diff)
```

## Single-read verification

Automated tests use `CountingSourceContentProvider` in `Fuse.Analysis.Tests` to assert each file is read at most once per fusion run when content is shared across graph building, focus resolution, and reduction.

## Reporting

When opening a PR for performance work, include:

- Fixture description (repo size, file count).
- Cold serial time, parallel time, and speedup ratio.
- Warm cache time when applicable.
- Hardware (CPU model, core count) and OS.
