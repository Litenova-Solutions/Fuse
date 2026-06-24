# Layer Ranking (B3 / B4): retrieval-quality metrics that isolate ranking from packing.
# For each PR we run query and focus at a budget large enough that nothing is dropped, so the emitted file
# order is the full ranked seed-plus-expansion list. Against that ranking we compute recall@k (k in 1,3,5,10,20),
# mean reciprocal rank (MRR), and nDCG@10. recall@k (B3) measures ranking quality before any budget cut; pairing
# it with layer2a's recall@budget (B4) tells you whether a miss is a ranking problem (the truth file is not
# ranked high) or a packing problem (it ranks high but the budget drops it).
#
# Output: results/layer-ranking.json, results/layer-ranking.md

. "$PSScriptRoot/common.ps1"

$RankBudget = 4000000   # large enough that packing never truncates the ranked list
$RankTop    = 50        # candidate seed pool; expansion adds neighbours on top
$Depth      = 2
$Ks         = @(1, 3, 5, 10, 20)

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json
$results = @()

# Emitted file paths in EMISSION ORDER (most-relevant first), which is the ranking we score.
function Get-OrderedPaths($outDir) {
    $f = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $m = [regex]::Matches($text, '<file path="([^"]+)"')
    return @($m | ForEach-Object { $_.Groups[1].Value -replace '\\','/' })
}

function Get-RankMetrics($ordered, $truth, [int[]]$ks) {
    $tr = @($truth | ForEach-Object { $_ -replace '\\','/' })
    $ord = @($ordered | Where-Object { $_ -like '*.cs' })
    $metrics = [ordered]@{}

    foreach ($k in $ks) {
        $topK = @($ord | Select-Object -First $k)
        $hit = @($tr | Where-Object { $topK -contains $_ }).Count
        $metrics["recall_at_$k"] = if ($tr.Count) { [math]::Round($hit / $tr.Count, 3) } else { 0 }
    }

    # MRR: reciprocal rank of the first truth file in the ranking (0 when none ranked).
    $mrr = 0.0
    for ($i = 0; $i -lt $ord.Count; $i++) {
        if ($tr -contains $ord[$i]) { $mrr = [math]::Round(1.0 / ($i + 1), 3); break }
    }
    $metrics['mrr'] = $mrr

    # nDCG@10 with binary relevance: DCG over the top 10, normalized by the ideal DCG for this truth count.
    $dcg = 0.0
    $limit = [math]::Min(10, $ord.Count)
    for ($i = 0; $i -lt $limit; $i++) {
        if ($tr -contains $ord[$i]) { $dcg += 1.0 / [math]::Log(($i + 2), 2) }
    }
    $ideal = 0.0
    $idealHits = [math]::Min(10, $tr.Count)
    for ($i = 0; $i -lt $idealHits; $i++) { $ideal += 1.0 / [math]::Log(($i + 2), 2) }
    $metrics['ndcg_at_10'] = if ($ideal -gt 0) { [math]::Round($dcg / $ideal, 3) } else { 0 }

    return [pscustomobject]$metrics
}

$repoGroups = $prs | Group-Object repo
foreach ($g in $repoGroups) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    Write-Host "=== $($g.Name) ==="

    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt-rank/$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { Write-Warning "  PR#$($pr.pr): worktree failed"; continue }

        $truth = @($pr.changed_cs)
        $srcChanged = @($truth | Where-Object { $_ -notmatch '(?i)test' })
        $seedFile = if ($srcChanged.Count) { $srcChanged[0] } else { $truth[0] }
        $seed = [System.IO.Path]::GetFileNameWithoutExtension($seedFile)
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        $modes = @(
            @{ name = 'query'; flags = @('--query', $q, '--query-top', "$RankTop", '--depth', "$Depth") }
            @{ name = 'focus'; flags = @('--focus', $seed, '--depth', "$Depth") }
        )

        foreach ($mode in $modes) {
            $outDir = Join-Path $ResultsDir ".rank/$($g.Name)_$($pr.pr)/$($mode.name)"
            if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
            New-Item -ItemType Directory -Force -Path $outDir | Out-Null
            $a = @('dotnet', '--directory', $wt, '--output', $outDir, '--overwrite',
                '--format', 'xml', '--tokenizer', 'o200k_base', '--no-cache',
                '--max-tokens', "$RankBudget", '--no-manifest') + $mode.flags
            $null = Measure-Process $Fuse $a
            $ordered = Get-OrderedPaths $outDir
            $m = Get-RankMetrics $ordered $truth $Ks
            $row = [ordered]@{ repo = $g.Name; pr = $pr.pr; mode = $mode.name; truth = $truth.Count; ranked = $ordered.Count }
            foreach ($p in $m.PSObject.Properties) { $row[$p.Name] = $p.Value }
            $results += [pscustomobject]$row
        }

        # Filter by repo as well: PR numbers are not unique across repositories.
        $qRow = $results | Where-Object { $_.repo -eq $g.Name -and $_.pr -eq $pr.pr -and $_.mode -eq 'query' } | Select-Object -Last 1
        $fRow = $results | Where-Object { $_.repo -eq $g.Name -and $_.pr -eq $pr.pr -and $_.mode -eq 'focus' } | Select-Object -Last 1
        Write-Host ("  PR#{0,-5} truth {1,2}  query r@5 {2:P0} mrr {3}  focus r@5 {4:P0} mrr {5}" -f `
            $pr.pr, $truth.Count, $qRow.recall_at_5, $qRow.mrr, $fRow.recall_at_5, $fRow.mrr)

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultsDir 'layer-ranking.json')

$agg = $results | Group-Object mode | ForEach-Object {
    $row = [ordered]@{ mode = $_.Group[0].mode; n = $_.Count }
    foreach ($k in $Ks) { $row["recall_at_$k"] = [math]::Round(($_.Group | Measure-Object "recall_at_$k" -Average).Average, 3) }
    $row['mrr'] = [math]::Round(($_.Group | Measure-Object mrr -Average).Average, 3)
    $row['ndcg_at_10'] = [math]::Round(($_.Group | Measure-Object ndcg_at_10 -Average).Average, 3)
    [pscustomobject]$row
}

$md = @('# Layer Ranking results (retrieval quality, budget-independent)','',
        "Query top $RankTop, depth $Depth, budget $RankBudget (large enough that packing never truncates). PRs: $($prs.Count).",
        'recall@k and MRR/nDCG score the ranked seed-plus-expansion list before any budget cut, so they isolate',
        'ranking quality from packing. Compare recall@k here with layer2a recall@budget: a truth file that ranks',
        'high (good recall@k) but misses at budget is a packing loss; one that ranks low is a ranking loss.','',
        ('| Mode | ' + (($Ks | ForEach-Object { "recall@$_" }) -join ' | ') + ' | MRR | nDCG@10 | N |'),
        ('|------|' + (($Ks | ForEach-Object { '------:' }) -join '|') + '|----:|--------:|--:|'))
foreach ($a in ($agg | Sort-Object mode)) {
    $cells = ($Ks | ForEach-Object { '{0:P0}' -f $a."recall_at_$_" }) -join ' | '
    $md += ('| {0} | {1} | {2} | {3} | {4} |' -f $a.mode, $cells, $a.mrr, $a.ndcg_at_10, $a.n)
}
$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer-ranking.md')
$agg | Format-Table | Out-String | Write-Host
Write-Host "Layer Ranking complete -> results/layer-ranking.{json,md}"
