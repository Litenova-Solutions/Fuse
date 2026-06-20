# One command to reproduce every benchmark layer end to end.
#   pwsh -File tests/benchmarks/harness/run-all.ps1
# Steps: build the CLI and the two measurement tools, clone the pinned corpus,
# regenerate the PR ground truth, then run layers 1, 2A, and 2B. Results land in
# tests/benchmarks/results.

. "$PSScriptRoot/common.ps1"

Write-Host "== building fuse + measurement tools =="
dotnet build (Join-Path $RepoRoot 'src/Host/Fuse.Cli/Fuse.Cli.csproj') -c Release -v q
dotnet build (Join-Path $RepoRoot 'tests/benchmarks/tools/TokenCount/TokenCount.csproj') -c Release -v q
dotnet build (Join-Path $RepoRoot 'tests/benchmarks/tools/Fidelity/Fidelity.csproj') -c Release -v q

& "$PSScriptRoot/setup-corpus.ps1"
& "$PSScriptRoot/gen-prs.ps1"
& "$PSScriptRoot/layer1.ps1"
& "$PSScriptRoot/layer2a.ps1"
& "$PSScriptRoot/layer2b.ps1"

Write-Host "All layers complete. See tests/benchmarks/results."
