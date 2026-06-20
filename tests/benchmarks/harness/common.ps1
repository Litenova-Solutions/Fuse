# Shared paths and helpers for the Fuse benchmark harness.
# Dot-source this from the layer scripts: . "$PSScriptRoot/common.ps1"

$ErrorActionPreference = 'Stop'

# Repo root is three levels up from this file (tests/benchmarks/harness).
$script:RepoRoot   = (Resolve-Path "$PSScriptRoot/../../..").Path
$script:BenchRoot  = (Resolve-Path "$PSScriptRoot/..").Path
$script:CorpusDir  = Join-Path $BenchRoot '.corpus'
$script:ResultsDir = Join-Path $BenchRoot 'results'
$script:CorpusJson = Join-Path $BenchRoot 'corpus.json'

$script:Fuse      = Join-Path $RepoRoot 'src/Host/Fuse.Cli/bin/Release/net10.0/fuse.exe'
$script:TokenCount = Join-Path $RepoRoot 'tests/benchmarks/tools/TokenCount/bin/Release/net10.0/tokencount.exe'
$script:Fidelity  = Join-Path $RepoRoot 'tests/benchmarks/tools/Fidelity/bin/Release/net10.0/fidelity.exe'

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

function Get-Corpus {
    (Get-Content $CorpusJson -Raw | ConvertFrom-Json).repos
}

function Resolve-RepoPath($repo) {
    if ($repo.local) { return (Join-Path $RepoRoot $repo.local) }
    return (Join-Path $CorpusDir $repo.name)
}

# Count tokens of a file with the shared o200k_base tokenizer.
function Get-Tokens($path) {
    $json = & $TokenCount $path | ConvertFrom-Json
    return [int]$json.total
}

# Run a command, returning wall-clock ms and peak working-set bytes of the child.
# Uses System.Diagnostics.Process directly so PeakWorkingSet64 is still readable
# after exit (Start-Process -PassThru reports 0 for an exited process).
function Measure-Process($exe, [string[]]$cmdArgs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    foreach ($a in $cmdArgs) { $psi.ArgumentList.Add([string]$a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.StandardOutput.ReadToEndAsync()
    $null = $p.StandardError.ReadToEndAsync()
    $peak = 0
    while (-not $p.HasExited) {
        try { $p.Refresh(); if ($p.PeakWorkingSet64 -gt $peak) { $peak = $p.PeakWorkingSet64 } } catch {}
        Start-Sleep -Milliseconds 15
    }
    try { $p.Refresh(); if ($p.PeakWorkingSet64 -gt $peak) { $peak = $p.PeakWorkingSet64 } } catch {}
    $p.WaitForExit()
    $sw.Stop()
    return [pscustomobject]@{
        Ms       = $sw.ElapsedMilliseconds
        PeakMB   = [math]::Round($peak / 1MB, 1)
        ExitCode = $p.ExitCode
    }
}

# All .cs files in a repo, excluding build output and generated files.
function Get-CsFiles($repoPath) {
    Get-ChildItem -Path $repoPath -Recurse -File -Filter *.cs |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
            $_.Name -notlike '*.g.cs' -and
            $_.Name -notlike '*.Designer.cs'
        }
}

# Stage a C#-only mirror of a repo (preserving relative paths) so every tool
# (raw concat, Fuse, Repomix) sees an identical file set. Returns the mirror path.
function New-CsMirror($repoPath, $name) {
    $mirror = Join-Path $ResultsDir ".mirror/$name"
    if (Test-Path $mirror) { Remove-Item -Recurse -Force $mirror }
    New-Item -ItemType Directory -Force -Path $mirror | Out-Null
    $repoFull = (Resolve-Path $repoPath).Path
    foreach ($f in Get-CsFiles $repoPath) {
        $rel = $f.FullName.Substring($repoFull.Length).TrimStart('\','/')
        $target = Join-Path $mirror $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $target
    }
    return $mirror
}
