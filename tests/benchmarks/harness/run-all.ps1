# One command to reproduce every benchmark layer end to end.
#   pwsh -File tests/benchmarks/harness/run-all.ps1
#   pwsh -File tests/benchmarks/harness/run-all.ps1 -Compare results/baseline.layer1.json
# Steps: build the CLI and the measurement tools, clone the pinned corpus,
# regenerate the PR ground truth, then run layers 1, 2A, 2B, the context-acquisition
# scenario layer (layer 4), and the illustrative layer 3 round-trip model. Results land
# in tests/benchmarks/results. With -Compare, the fresh layer-1 result is diffed against
# the given baseline and the run fails on any reduction, fidelity, or body-integrity
# regression beyond tolerance.
#
# Prerequisites:
#   - dotnet SDK 10.0+ and git: required for every Fuse-versus-raw number (runs offline
#     once the corpus is cloned).
#   - network: required once to clone the pinned corpus, and for the Repomix arm.
#   - npx (Node): required for the Repomix (generic-packer) arm in layer 1 and layer 4.
# Without npx, everything still runs and every Fuse-versus-raw number is valid; only the
# Fuse-versus-generic-packer rows are carried from the committed baseline (layer 1) or
# omitted (layer 4), never stubbed.

param(
    [string]$Compare = ''
)

. "$PSScriptRoot/common.ps1"

Write-Host "== building fuse + measurement tools =="
dotnet build (Join-Path $RepoRoot 'src/Host/Fuse.Cli/Fuse.Cli.csproj') -c Release -v q
dotnet build (Join-Path $RepoRoot 'tests/benchmarks/tools/TokenCount/TokenCount.csproj') -c Release -v q
dotnet build (Join-Path $RepoRoot 'tests/benchmarks/tools/Fidelity/Fidelity.csproj') -c Release -v q
dotnet build (Join-Path $RepoRoot 'tests/benchmarks/tools/BodyIntegrity/BodyIntegrity.csproj') -c Release -v q

& "$PSScriptRoot/setup-corpus.ps1"
& "$PSScriptRoot/gen-prs.ps1"
& "$PSScriptRoot/layer1.ps1"
& "$PSScriptRoot/layer2a.ps1"
& "$PSScriptRoot/layer4-scenario.ps1"
& "$PSScriptRoot/layer2b.ps1"
& "$PSScriptRoot/layer3.ps1"

if ($Compare) {
    $baselinePath = if ([System.IO.Path]::IsPathRooted($Compare)) { $Compare } else { Join-Path $RepoRoot $Compare }
    $current  = Get-Content (Join-Path $ResultsDir 'layer1.json') -Raw | ConvertFrom-Json
    $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
    $cmp = Compare-Results $current $baseline
    if (-not $cmp.ok) {
        Write-Host "REGRESSION vs baseline:" -ForegroundColor Red
        foreach ($r in $cmp.regressions) {
            Write-Host ("  {0} {1}: {2} -> {3}" -f $r.key, $r.metric, $r.baseline, $r.current)
        }
        throw "Benchmark regression detected against $baselinePath"
    }
    Write-Host "No regression vs baseline."
}

Write-Host "All layers complete. See tests/benchmarks/results."
