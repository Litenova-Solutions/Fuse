# Spike (item 8): does the coarse project-reference graph lift recall? It links a seed to candidate files in
# projects its .csproj references or is referenced by, so a seed can reach a related file across an assembly
# boundary (for example a changed library file and an integration test that does not name its type) that the
# intra-project type graph misses. Two arms over the same PRs layer2a uses, at the headline budget, for query
# and focus (the modes that expand): FUSE_PROJECT_GRAPH off vs on. Prints per-repo and overall recall deltas.
# The plan flags AutoMapper and Newtonsoft as the validation repos. Not committed results.

. "$PSScriptRoot/common.ps1"

$Budget = 50000
$Depth  = 2
$Modes  = @('query', 'focus')

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
function Run-Arm($wt, $mode, $q, $projectGraph) {
    $outDir = Join-Path $ResultsDir ".spikepg/$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $env:FUSE_PROJECT_GRAPH = $projectGraph
    $a = @('dotnet','--directory',$wt,'--output',$outDir,'--overwrite','--format','xml','--tokenizer','o200k_base',
        '--no-cache','--no-manifest','--max-tokens',"$Budget",'--depth',"$Depth")
    if ($mode -eq 'query') { $a += @('--query',$q,'--query-top','10') }
    else { $seed = [System.IO.Path]::GetFileNameWithoutExtension(($q -split '\s+')[0]); $a += @('--focus',$q) }
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
        $wt = Join-Path $ResultsDir ".wt/pg_$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { continue }

        $truth = @($pr.changed_cs)
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        foreach ($mode in $Modes) {
            $off = Measure-Recall (Run-Arm $wt $mode $q '0') $truth
            $on  = Measure-Recall (Run-Arm $wt $mode $q '1') $truth
            $rows += [pscustomobject]@{ repo=$g.Name; mode=$mode; pr=$pr.pr; off=$off; on=$on }
        }
        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}
Remove-Item Env:\FUSE_PROJECT_GRAPH -ErrorAction SilentlyContinue

foreach ($mode in $Modes) {
    $mr = $rows | Where-Object { $_.mode -eq $mode }
    Write-Host "`n=== $mode (budget $Budget): per-repo mean recall ==="
    foreach ($grp in ($mr | Group-Object repo)) {
        $mo = ($grp.Group | Measure-Object off -Average).Average
        $mn = ($grp.Group | Measure-Object on  -Average).Average
        Write-Host ("  {0,-18} off {1:P0}  on {2:P0}  d {3:+0%;-0%; 0%}" -f $grp.Name, $mo, $mn, ($mn-$mo))
    }
    $ao = ($mr | Measure-Object off -Average).Average
    $an = ($mr | Measure-Object on  -Average).Average
    Write-Host ("  {0,-18} off {1:P0}  on {2:P0}  d {3:+0%;-0%; 0%}" -f 'OVERALL', $ao, $an, ($an-$ao))
}
