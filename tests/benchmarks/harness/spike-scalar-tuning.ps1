# Spike (item 5): scalar admission tuning sweep over the query path.
# Sweeps the four scalars the plan names (CentralityWeight, HopDecay, query-top, PRF ExpansionWeight)
# one-at-a-time around the defaults, plus an "admit more" combo, over the 90-PR corpus. Reports mean
# query recall@50k on the dev and test folds (B5 split: even PR id = test, odd = dev). The discipline:
# pick the best arm on dev, then read its test-fold recall, and reject any arm that regresses a per-repo
# test cell versus the baseline. Throwaway A/B; prints a table, writes nothing committed except when the
# operator pipes the summary into the results note by hand.

. "$PSScriptRoot/common.ps1"

$Budget = 50000
$Depth  = 2

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json

# Arms: each sets the four scalars. The baseline is the shipped default; every other arm moves one scalar
# (or, for the last, several in the "admit a wider neighbourhood" direction) so the sweep is interpretable.
$arms = @(
    @{ name='baseline';      cw=0.15; hd=0.5; ew=0.2;  top=10 },
    @{ name='cw=0.0';        cw=0.0;  hd=0.5; ew=0.2;  top=10 },
    @{ name='cw=0.30';       cw=0.30; hd=0.5; ew=0.2;  top=10 },
    @{ name='hd=0.4';        cw=0.15; hd=0.4; ew=0.2;  top=10 },
    @{ name='hd=0.6';        cw=0.15; hd=0.6; ew=0.2;  top=10 },
    @{ name='ew=0.1';        cw=0.15; hd=0.5; ew=0.1;  top=10 },
    @{ name='ew=0.35';       cw=0.15; hd=0.5; ew=0.35; top=10 },
    @{ name='top=8';         cw=0.15; hd=0.5; ew=0.2;  top=8  },
    @{ name='top=12';        cw=0.15; hd=0.5; ew=0.2;  top=12 },
    @{ name='admit-more';    cw=0.30; hd=0.6; ew=0.2;  top=12 }
)

function Get-EmittedPaths($outDir) {
    $f = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $m = [regex]::Matches($text, '<file path="([^"]+)"')
    return @($m | ForEach-Object { $_.Groups[1].Value -replace '\\','/' })
}

function Measure-Recall($emitted, $truth) {
    $em = @($emitted | ForEach-Object { $_ -replace '\\','/' })
    $tr = @($truth   | ForEach-Object { $_ -replace '\\','/' })
    $hit = @($tr | Where-Object { $em -contains $_ })
    if ($tr.Count) { return [math]::Round($hit.Count / $tr.Count, 3) } else { return 0 }
}

function Get-Split($prId) {
    if (([int]$prId % 2) -eq 0) { return 'test' }
    return 'dev'
}

function Run-Arm($wt, $q, $arm) {
    $outDir = Join-Path $ResultsDir ".spike/$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $env:FUSE_CENTRALITY_WEIGHT = "$($arm.cw)"
    $env:FUSE_HOP_DECAY         = "$($arm.hd)"
    $env:FUSE_EXPANSION_WEIGHT  = "$($arm.ew)"
    $a = @('dotnet','--directory', $wt, '--output', $outDir, '--overwrite',
        '--format','xml','--tokenizer','o200k_base','--no-cache',
        '--max-tokens', "$Budget", '--no-manifest',
        '--query', $q, '--query-top',"$($arm.top)",'--depth',"$Depth")
    $null = Measure-Process $Fuse $a
    $emitted = Get-EmittedPaths $outDir
    Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
    return $emitted
}

$rows = @()
foreach ($g in ($prs | Group-Object repo)) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt/scalar_$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { continue }

        $truth = @($pr.changed_cs)
        if (-not $truth.Count) { git -C $repoPath worktree remove --force $wt 2>$null; continue }
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }
        $split = Get-Split $pr.pr

        foreach ($arm in $arms) {
            $r = Measure-Recall (Run-Arm $wt $q $arm) $truth
            $rows += [pscustomobject]@{ arm=$arm.name; repo=$g.Name; pr=$pr.pr; split=$split; recall=$r }
        }
        Write-Host ("  {0,-16} PR#{1,-5} ({2})" -f $g.Name, $pr.pr, $split)

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}
Remove-Item Env:\FUSE_CENTRALITY_WEIGHT -ErrorAction SilentlyContinue
Remove-Item Env:\FUSE_HOP_DECAY -ErrorAction SilentlyContinue
Remove-Item Env:\FUSE_EXPANSION_WEIGHT -ErrorAction SilentlyContinue

function Mean($vals) { if ($vals.Count) { return [math]::Round((($vals | Measure-Object -Average).Average), 3) } else { return 0 } }

Write-Host "`n=== Arm dev/test mean query recall @ $Budget (tune on dev, read test) ==="
Write-Host ("  {0,-12} {1,6} {2,6}" -f 'arm','dev','test')
$baseTest = $null
$summary = @()
foreach ($arm in $arms) {
    $dev  = Mean @($rows | Where-Object { $_.arm -eq $arm.name -and $_.split -eq 'dev'  } | ForEach-Object { $_.recall })
    $test = Mean @($rows | Where-Object { $_.arm -eq $arm.name -and $_.split -eq 'test' } | ForEach-Object { $_.recall })
    if ($arm.name -eq 'baseline') { $baseTest = $test }
    $summary += [pscustomobject]@{ arm=$arm.name; dev=$dev; test=$test }
    Write-Host ("  {0,-12} {1,6:P0} {2,6:P0}" -f $arm.name, $dev, $test)
}

# Pick the best arm on dev (excluding baseline ties), then judge it honestly on test and per-repo.
$bestDev = $summary | Where-Object { $_.arm -ne 'baseline' } | Sort-Object dev -Descending | Select-Object -First 1
Write-Host "`n=== Verdict ==="
Write-Host ("  best dev arm: {0} (dev {1:P0}, test {2:P0}); baseline test {3:P0}" -f $bestDev.arm, $bestDev.dev, $bestDev.test, $baseTest)

# Per-repo test regression check against the baseline (the B9 discipline: no per-repo drop).
$repos = @($rows.repo | Select-Object -Unique | Sort-Object)
$regressed = @()
foreach ($repo in $repos) {
    $b = Mean @($rows | Where-Object { $_.arm -eq 'baseline' -and $_.repo -eq $repo -and $_.split -eq 'test' } | ForEach-Object { $_.recall })
    $c = Mean @($rows | Where-Object { $_.arm -eq $bestDev.arm -and $_.repo -eq $repo -and $_.split -eq 'test' } | ForEach-Object { $_.recall })
    $mark = if ($c + 0.02 -lt $b) { $regressed += $repo; 'REGRESS' } else { 'ok' }
    Write-Host ("  {0,-18} baseline {1:P0}  {2} {3:P0}  {4}" -f $repo, $b, $bestDev.arm, $c, $mark)
}
if ($bestDev.test + 0.01 -ge $baseTest -and $regressed.Count -eq 0) {
    Write-Host "`n  ADOPT-CANDIDATE: best dev arm holds on test with no per-repo regression. Validate with a full layer2a run before changing a default."
} else {
    Write-Host "`n  KEEP DEFAULTS: best dev arm does not clear baseline on the held-out test fold or regresses a per-repo cell. Tuning would overfit dev."
}
