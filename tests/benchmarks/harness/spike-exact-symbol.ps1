# Spike: compare query-mode recall with the exact-declared-symbol boost (Q3) ON vs OFF, across all three
# budgets. Fast, throwaway A/B over the same PRs layer2a uses, query mode only. The boost lifts a candidate
# that declares a type or member the query names exactly above files that merely mention the words; this
# checks whether it helps recall at any budget without a per-repo regression at the 50k headline.
# Not committed results; prints per-budget per-repo and overall recall deltas.

. "$PSScriptRoot/common.ps1"

$Budgets = @(10000, 25000, 50000)
$Depth   = 2

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json

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

function Run-Mode($wt, $q, $budget, $exactBoost) {
    $outDir = Join-Path $ResultsDir ".spike/$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $env:FUSE_EXACT_SYMBOL_BOOST = $exactBoost
    $a = @('dotnet','--directory', $wt, '--output', $outDir, '--overwrite',
        '--format','xml','--tokenizer','o200k_base','--no-cache',
        '--max-tokens', "$budget", '--no-manifest',
        '--query', $q, '--query-top','10','--depth',"$Depth")
    $null = Measure-Process $Fuse $a
    $emitted = Get-EmittedPaths $outDir
    Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
    return $emitted
}

# rows: one record per (budget, pr) with off/on recall.
$rows = @()
$repoGroups = $prs | Group-Object repo
foreach ($g in $repoGroups) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt/spikee_$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { continue }

        $truth = @($pr.changed_cs)
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        foreach ($b in $Budgets) {
            $off = Measure-Recall (Run-Mode $wt $q $b '0') $truth
            $on  = Measure-Recall (Run-Mode $wt $q $b '1') $truth
            $rows += [pscustomobject]@{ budget=$b; repo=$g.Name; pr=$pr.pr; off=$off; on=$on; delta=[math]::Round($on-$off,3) }
        }
        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}
Remove-Item Env:\FUSE_EXACT_SYMBOL_BOOST -ErrorAction SilentlyContinue

foreach ($b in $Budgets) {
    $br = $rows | Where-Object { $_.budget -eq $b }
    Write-Host "`n=== Budget $b : per-repo mean recall ==="
    foreach ($grp in ($br | Group-Object repo)) {
        $moff = ($grp.Group | Measure-Object off -Average).Average
        $mon  = ($grp.Group | Measure-Object on  -Average).Average
        Write-Host ("  {0,-18} off {1:P0}  on {2:P0}  d {3:+0%;-0%; 0%}" -f $grp.Name, $moff, $mon, ($mon-$moff))
    }
    $aoff = ($br | Measure-Object off -Average).Average
    $aon  = ($br | Measure-Object on  -Average).Average
    Write-Host ("  {0,-18} off {1:P0}  on {2:P0}  d {3:+0%;-0%; 0%}" -f 'OVERALL', $aoff, $aon, ($aon-$aoff))
}
