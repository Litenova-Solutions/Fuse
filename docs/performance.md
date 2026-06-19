# Performance and cold start

Fuse is optimized for one-shot CLI invocations (`fuse dotnet`, `fuse init`, template commands). Each run pays a fixed cost before pipeline work begins: process startup, dependency injection graph construction, and tokenizer initialization. On small repositories that fixed cost can dominate wall time.

Long-running commands such as `fuse serve` (MCP) are less sensitive to cold start. The MCP server stays resident; startup is paid once per session.

## What was optimized

| Area | Change | Effect |
|------|--------|--------|
| Tokenizer | `Microsoft.ML.Tokenizers` replaces TiktokenSharp | Faster init, accurate `o200k_base` / `cl100k_base`, AOT-friendly |
| Native AOT | Per-RID published binaries + framework-dependent fallback | Large reduction in JIT and startup time on .NET 10+ |
| JSON | Source-generated `System.Text.Json` contexts | Trim/AOT-safe config and JSON output |
| Regex | `[GeneratedRegex]` for remaining dynamic patterns | No runtime regex compiler |
| Allocations | `ArrayPool` for binary probe; span line scans | Lower allocation volume on large repos |

Deferred: ConsoleAppFramework migration, channel-based pipeline streaming.

## Distribution model

| Package | Role |
|---------|------|
| `Fuse` | Framework-dependent dotnet tool with `RollForward=LatestMajor` and ReadyToRun (portable fallback) |
| `Fuse.Runtime.win-x64` (etc.) | Native AOT binary for that RID; used by .NET 10+ enhanced tool resolution when available |
| Windows installer | Ships AOT `fuse.exe` from the release workflow |

Build all packages locally:

```powershell
./build/pack-aot.ps1 -Version 2.0.0
```

Output lands in `src/Fuse.Cli/nupkg/`. AOT publish profiles live under `src/Fuse.Cli/Properties/PublishProfiles/`.

Publish a single RID:

```powershell
dotnet publish src/Fuse.Cli/Fuse.Cli.csproj -c Release /p:PublishProfile=aot-win-x64
```

Native AOT on Linux requires `clang` and `zlib1g-dev`.

## Benchmark methodology

Measure **fresh-process** wall time, not warm in-shell repeats. JIT tiering makes second invocations misleading.

### PowerShell (Windows)

```powershell
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath "path/to/fuse.exe" `
    -ArgumentList "dotnet","--directory","tests/fixtures/SampleShop","--output","C:\temp\fuse-bench","--overwrite","--format","xml","--tokenizer","o200k_base" `
    -PassThru -NoNewWindow -Wait
$sw.Stop()
"$($sw.ElapsedMilliseconds) ms (exit $($p.ExitCode))"
```

### BenchmarkDotNet (optional)

Use `RunStrategy=ColdStart` and `WarmupCount=0` when comparing CLI frameworks or pre/post AOT builds.

### Fixtures

Use `tests/fixtures/SampleShop` for small-repo comparisons. For throughput work, use a medium monorepo (thousands of source files). See [tests/benchmarks/README.md](../tests/benchmarks/README.md).

Record: fixture size, command line, hardware, OS, cold wall time, pipeline duration from the `Stats:` line.

## CI validation

The `aot` job in `.github/workflows/ci.yml` publishes Native AOT for `win-x64` and `linux-x64`, then smoke-runs:

- `fuse init --help`
- `fuse dotnet` on SampleShop with required `--format` and `--tokenizer`

Trim/AOT builds treat IL2026 and IL3050 as errors in project code.

## MCP and AOT

`fuse serve` ships in the same binary as one-shot commands. If a future MCP SDK release fails AOT trimming, the fallback is a framework-dependent tool install for MCP workloads while AOT covers fusion commands.
