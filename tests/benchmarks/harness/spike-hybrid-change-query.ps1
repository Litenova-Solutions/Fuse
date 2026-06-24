# Spike: does combining change scoping with query scoping help (item 32)? Change mode already reaches high
# recall but low precision and misses the unchanged interfaces/callers/tests a diff never shows. This runs
# change-only and query-only per PR at the headline budget and reports change recall, query recall, and the
# union recall (the ceiling a perfect change+query merge could reach). If union recall is not much above
# change recall, a hybrid cannot help recall and its only case is precision; if it is, the hybrid is worth
# building. Reports overall and by change-set size, since change mode is weakest on large change sets.
# Throwaway: changes no production code, uses the shipped CLI.

. "$PSScriptRoot/common.ps1"

$Budget = 50000
$Depth  = 2

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json

function Get-EmittedPaths($outDir) {
    $f = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $m = [regex]::Matches($text, '<file path="([^"]+)"')
    return @($m | ForEach-Object { $_.Groups[1].Value -replace '\\','/' })
}

function Recall($emitted, $truth) {
    $em = @($emitted | ForEach-Object { $_ -replace '\\','/' })
    $tr = @($truth   | ForEach-Object { $_ -replace '\\','/' })
    $hit = @($tr | Where-Object { $em -contains $_ })
    if ($tr.Count) { return [math]::Round($hit.Count / $tr.Count, 3) } else { return 0 }
}

function Run-Fuse($wt, [string[]]$scope) {
    $outDir = Join-Path $ResultsDir ".spikeh/$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $a = @('dotnet','--directory', $wt, '--output', $outDir, '--overwrite',
        '--format','xml','--tokenizer','o200k_base','--no-cache','--no-manifest',
        '--max-tokens', "$Budget", '--level','standard') + $scope
    $null = Measure-Process $Fuse $a
    $emitted = Get-EmittedPaths $outDir
    Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
    return $emitted
}

function Stratum($n) { if ($n -le 3) { 'small' } elseif ($n -le 9) { 'medium' } else { 'large' } }

$rows = @()
$repoGroups = $prs | Group-Object repo
foreach ($g in $repoGroups) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt/spikeh_$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { continue }

        $truth = @($pr.changed_cs)
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        $chEmit = Run-Fuse $wt @('--changed-since', $pr.base, '--include-dependents')
        $qEmit  = Run-Fuse $wt @('--query', $q, '--query-top','10','--depth',"$Depth")
        $union  = @($chEmit + $qEmit | Select-Object -Unique)

        $rows += [pscustomobject]@{
            repo=$g.Name; pr=$pr.pr; stratum=(Stratum $truth.Count)
            change=(Recall $chEmit $truth); query=(Recall $qEmit $truth); union=(Recall $union $truth)
        }
        Write-Host ("  {0} PR#{1,-5} change {2:P0}  query {3:P0}  union {4:P0}  ({5})" -f `
            $g.Name, $pr.pr, ($rows[-1].change), ($rows[-1].query), ($rows[-1].union), $rows[-1].stratum)

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

Write-Host "`n=== Overall mean recall ==="
$mc = ($rows | Measure-Object change -Average).Average
$mq = ($rows | Measure-Object query  -Average).Average
$mu = ($rows | Measure-Object union  -Average).Average
Write-Host ("  change {0:P0}  query {1:P0}  union {2:P0}  (union lift over change {3:+0%;-0%; 0%})" -f $mc, $mq, $mu, ($mu-$mc))

Write-Host "`n=== By change-set size ==="
foreach ($s in @('small','medium','large')) {
    $sr = $rows | Where-Object { $_.stratum -eq $s }
    if (-not $sr) { continue }
    $sc = ($sr | Measure-Object change -Average).Average
    $su = ($sr | Measure-Object union  -Average).Average
    Write-Host ("  {0,-7} n={1,2}  change {2:P0}  union {3:P0}  (lift {4:+0%;-0%; 0%})" -f $s, @($sr).Count, $sc, $su, ($su-$sc))
}
