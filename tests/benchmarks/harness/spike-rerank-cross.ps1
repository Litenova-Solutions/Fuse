# Spike (item 11): does the cross-encoder reranker clear about 60 percent on the hard repos (AutoMapper,
# NewtonsoftJson) at the headline budget, where the bi-encoder (item 9) did not? Three arms over the same PRs
# layer2a uses, query mode only: lexical floor (off), bi-encoder (FUSE_RERANK=1), and cross-encoder
# (FUSE_RERANK=1 + FUSE_RERANK_MODEL=cross). The cross-encoder runs the model once per candidate, so it is the
# slow arm. Prints per-repo and overall recall for each arm. Not committed results.

. "$PSScriptRoot/common.ps1"

$Budget = 50000
$Depth  = 2
$HardRepos = @('AutoMapper', 'NewtonsoftJson')

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json

function Get-EmittedPaths($outDir) {
    $f = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $m = [regex]::Matches([System.IO.File]::ReadAllText($f.FullName), '<file path="([^"]+)"')
    return @($m | ForEach-Object { $_.Groups[1].Value -replace '\\','/' })
}
function Measure-Recall($emitted, $truth) {
    $em = @($emitted | ForEach-Object { $_ -replace '\\','/' })
    $tr = @($truth   | ForEach-Object { $_ -replace '\\','/' })
    $hit = @($tr | Where-Object { $em -contains $_ })
    if ($tr.Count) { return [math]::Round($hit.Count / $tr.Count, 3) } else { return 0 }
}
function Run-Arm($wt, $q, $rerank, $model) {
    $outDir = Join-Path $ResultsDir ".spikecross/$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $env:FUSE_RERANK = $rerank
    if ($model) { $env:FUSE_RERANK_MODEL = $model } else { Remove-Item Env:\FUSE_RERANK_MODEL -ErrorAction SilentlyContinue }
    $a = @('dotnet','--directory',$wt,'--output',$outDir,'--overwrite','--format','xml','--tokenizer','o200k_base',
        '--no-cache','--no-manifest','--max-tokens',"$Budget",'--query',$q,'--query-top','10','--depth',"$Depth")
    $null = Measure-Process $Fuse $a
    $emitted = Get-EmittedPaths $outDir
    Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
    return $emitted
}

$rows = @()
foreach ($g in ($prs | Group-Object repo | Where-Object { $HardRepos -contains $_.Name })) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt/cross_$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { continue }

        $truth = @($pr.changed_cs)
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        $off   = Measure-Recall (Run-Arm $wt $q '0' $null)    $truth
        $bi    = Measure-Recall (Run-Arm $wt $q '1' $null)    $truth
        $cross = Measure-Recall (Run-Arm $wt $q '1' 'cross')  $truth
        $rows += [pscustomobject]@{ repo=$g.Name; pr=$pr.pr; off=$off; bi=$bi; cross=$cross }
        Write-Host ("  {0} #{1,-5} off {2:P0}  bi {3:P0}  cross {4:P0}" -f $g.Name, $pr.pr, $off, $bi, $cross)

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}
Remove-Item Env:\FUSE_RERANK -ErrorAction SilentlyContinue
Remove-Item Env:\FUSE_RERANK_MODEL -ErrorAction SilentlyContinue

Write-Host "`n=== Budget $Budget : per-repo mean recall ==="
foreach ($grp in ($rows | Group-Object repo)) {
    $mo = ($grp.Group | Measure-Object off   -Average).Average
    $mb = ($grp.Group | Measure-Object bi    -Average).Average
    $mc = ($grp.Group | Measure-Object cross -Average).Average
    Write-Host ("  {0,-18} off {1:P0}  bi {2:P0}  cross {3:P0}" -f $grp.Name, $mo, $mb, $mc)
}
$ao = ($rows | Measure-Object off   -Average).Average
$ab = ($rows | Measure-Object bi    -Average).Average
$ac = ($rows | Measure-Object cross -Average).Average
Write-Host ("  {0,-18} off {1:P0}  bi {2:P0}  cross {3:P0}" -f 'OVERALL', $ao, $ab, $ac)
