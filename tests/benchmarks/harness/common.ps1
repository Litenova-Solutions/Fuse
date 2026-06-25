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
$script:BodyIntegrity = Join-Path $RepoRoot 'tests/benchmarks/tools/BodyIntegrity/bin/Release/net10.0/bodyintegrity.exe'

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

function Get-Corpus {
    (Get-Content $CorpusJson -Raw | ConvertFrom-Json).repos
}

# Decide whether a merged-PR title plausibly describes its C# change, used to drop title/diff-mismatch
# PRs from the Layer 2A/4/5 ground truth. A merge commit can bundle a real code change under a
# maintenance title (a dependency bump, a CI/build tweak, a release commit), so the title points away
# from the C# files the PR actually touched. Those PRs are honest noise: a title-keyword scope cannot
# find their change set because the title is about infrastructure, not the code, and unlike a bare
# "Merge branch ..." subject there is no recovery (the layers' merge-noise fallback substitutes the
# changed type names only when the title matches ^merge). We drop the misleading-maintenance titles by
# pattern only; the change set itself is left untouched. Merge-noise titles are deliberately NOT dropped
# here: the layers already handle them with a type-name fallback and report them as a B7 adversarial
# split. Returns $true when the title looks like a real code-change description, $false when it looks
# like a misleading maintenance title.
#
# Examples this rejects (surfaced by Layer 5 as 0%-recall noise):
#   "ci: skip Azure login and signing on PRs ..."          (AutoMapper#4634, a CI title over licensing code)
#   "Update Microsoft.Sbom.DotNetTool from 1.2.0 to 4.1.5"  (AutoMapper#4616, a tool bump over unrelated code)
function Test-PrTitleRelevant([string]$title) {
    if (-not $title) { return $false }
    $t = $title.Trim()
    # Misleading-maintenance patterns (case-insensitive). Each points at infrastructure or dependency
    # housekeeping, not the C# diff, so a title-keyword scope would miss the change set and there is no
    # merge-noise fallback to recover it.
    $noise = @(
        '^(ci|build|chore|deps|style|release)(\(|:)', # conventional-commit infra prefixes (ci:, build(deps):, chore:)
        '^ci\b',                                      # bare "ci ..." prefix
        '^(bump|upgrade)\b',                          # version-bump prefix
        '\bdependabot\b',                             # dependabot-authored bumps
        '\bfrom\s+v?\d[\w.\-]*\s+to\s+v?\d[\w.\-]*'   # "... from 1.2.0 to 4.1.5" version-bump phrasing
    )
    foreach ($p in $noise) { if ($t -imatch $p) { return $false } }
    return $true
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

# Body-integrity for one fused file: fraction of source string literals that survive byte-intact, and
# (when -ParseCheck) whether the output still parses. Returns the parsed JSON object.
function Get-BodyIntegrity($sourceDir, $fusedFile, [switch]$ParseCheck) {
    $biArgs = @($sourceDir, $fusedFile)
    if ($ParseCheck) { $biArgs += '--parse-check' }
    return (& $BodyIntegrity @biArgs | ConvertFrom-Json)
}

# Compare a fresh results object against a committed baseline and fail on regression beyond tolerance.
# `current` and `baseline` are arrays of row objects keyed by (repo, arm). Metrics checked: reduction_ratio
# (must not drop), types_ratio / methods_ratio / routes_ratio (fidelity must not drop), and
# literals_intact_ratio (body-integrity must not drop). Returns a result object with .ok and .regressions.
function Compare-Results($current, $baseline, [double]$Tolerance = 0.01) {
    $regressions = @()
    $baseByKey = @{}
    foreach ($b in $baseline) { $baseByKey["$($b.repo)/$($b.arm)"] = $b }

    foreach ($c in $current) {
        $key = "$($c.repo)/$($c.arm)"
        if (-not $baseByKey.ContainsKey($key)) { continue }
        $b = $baseByKey[$key]

        foreach ($metric in @('reduction_ratio','types_ratio','methods_ratio','routes_ratio','literals_intact_ratio')) {
            $cv = $c.$metric; $bv = $b.$metric
            if ($null -eq $cv -or $null -eq $bv) { continue }
            if (($bv - $cv) -gt $Tolerance) {
                $regressions += [pscustomobject]@{ key = $key; metric = $metric; baseline = $bv; current = $cv }
            }
        }

        # Body-integrity parse check is a hard gate: a true->false flip is always a regression.
        if ($null -ne $b.parses -and $b.parses -eq $true -and $c.parses -eq $false) {
            $regressions += [pscustomobject]@{ key = $key; metric = 'parses'; baseline = $true; current = $false }
        }
    }

    return [pscustomobject]@{ ok = ($regressions.Count -eq 0); regressions = $regressions }
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

# Run the `claude` CLI headlessly with a wall-clock backstop and a clean process-TREE teardown. Shared by the
# model-dependent layers (Layer 5 agent, Layer 6 peers) and the Layer 5 spike. Returns $true if claude exited
# on its own within $timeoutSec, $false if it had to be killed (wedged or timed out); stdout is written to
# $outFile (stderr to "$outFile.err").
#
# Why .NET Process and not Start-Process: Start-Process -ArgumentList re-quotes an array and splits a multi-word
# argument (it truncated the -p prompt to its first word in testing). ProcessStartInfo.ArgumentList builds the
# command line with correct per-argument quoting. Why Kill($true): claude spawns the MCP server (fuse mcp serve
# / codegraph / serena) as a child; killing claude alone would orphan it, and a live orphaned server holds the
# worktree's SQLite WAL lock so the next cold run blocks on it -- the exact leak that wedged the prior run.
# Stdin is redirected and closed so `claude -p` does not stall waiting on inherited console stdin.
function Invoke-ClaudeBounded([string[]]$argv, [string]$outFile, [string]$workingDir, [int]$timeoutSec) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'claude'
    foreach ($a in $argv) { [void]$psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $workingDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $outSb = [System.Text.StringBuilder]::new()
    $errSb = [System.Text.StringBuilder]::new()
    # Async reads so a large transcript cannot fill the OS pipe buffer and deadlock while we WaitForExit.
    $outEvt = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -MessageData $outSb -Action {
        if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
    $errEvt = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -MessageData $errSb -Action {
        if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.AppendLine($EventArgs.Data) } }
    try {
        [void]$proc.Start()
        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()
        $proc.StandardInput.Close()   # EOF on stdin so claude -p proceeds immediately

        $exited = $proc.WaitForExit($timeoutSec * 1000)
        if (-not $exited) {
            Write-Warning ("    claude exceeded {0}s wall clock; killing tree (pid {1}) and omitting." -f $timeoutSec, $proc.Id)
            try { $proc.Kill($true) } catch { }
            [void]$proc.WaitForExit(5000)
        } else {
            [void]$proc.WaitForExit()  # flush any async output buffered after the timed wait returned
        }
    }
    finally {
        Unregister-Event -SourceIdentifier $outEvt.Name -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $errEvt.Name -ErrorAction SilentlyContinue
    }
    $outSb.ToString() | Set-Content $outFile
    if ($errSb.Length) { $errSb.ToString() | Set-Content "$outFile.err" }
    $proc.Dispose()
    return $exited
}
