# Layer 2A: scoping recall and precision against real merged-PR change sets.
# For each PR (ground truth = the .cs files it changed) we reconstruct the head
# state in a git worktree and measure, at a fixed token budget:
#   - changes  : fuse --changed-since <base>
#   - focus    : fuse --focus <a changed type>
#   - query    : fuse --query "<PR title>"
#   - grep     : agent-native baseline (rank files by query-term hits, fill budget)
# Recall (necessary files included) is the headline; precision is reported too.
#
# Output: results/layer2a.json, results/layer2a.md

. "$PSScriptRoot/common.ps1"

# recall@budget is measured at several budgets so Phase 2/3 deltas can be read across the budget curve, not
# just at one point. The 50k budget remains the headline reported in the docs.
$Budgets = @(10000, 25000, 50000)
$Budget  = 50000    # headline budget (also the grep-baseline budget per level)
$Depth   = 2        # dependency expansion depth for focus/query
$stop = @('the','and','for','from','into','with','this','that','when','then','add','fix',
          'update','merge','pull','request','branch','copilot','use','via','not','are','was')

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json
$results = @()

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
    $recall = if ($tr.Count) { [math]::Round($hit.Count / $tr.Count, 3) } else { 0 }
    $emCs = @($em | Where-Object { $_ -like '*.cs' })
    $precision = if ($emCs.Count) { [math]::Round($hit.Count / $emCs.Count, 3) } else { 0 }
    return [pscustomobject]@{ recall = $recall; precision = $precision; hits = $hit.Count; emitted = $emCs.Count }
}

# B10 change-set-size stratum from the ground-truth file count: the budget wall mostly bites the large stratum,
# which the overall mean hides.
function Get-Stratum($truthCount) {
    if ($truthCount -le 3) { return 'small (1-3)' }
    if ($truthCount -le 9) { return 'medium (4-9)' }
    return 'large (10+)'
}

# B5 held-out split: assign each PR to a dev or test fold by the parity of its PR id, a fixed deterministic
# hash so the split never moves between runs and every repository contributes to both folds. Tune only on dev;
# publish test-set numbers. With the small 24-PR corpus this is methodology scaffolding, most useful once the
# corpus grows (B2) and before any scalar tuning (item 5).
function Get-Split($prId) {
    if (([int]$prId % 2) -eq 0) { return 'test' }
    return 'dev'
}

# B8 wasted tokens: the tokens spent on emitted files that were not in the truth set. Approximated from the
# emitted-file fractions (the harness counts whole-output tokens, not per-file), so it is a proportional
# estimate of budget spent off-target, not an exact per-file figure.
function Get-WastedTokens($tokens, $emitted, $hits) {
    if ($emitted -le 0) { return 0 }
    return [math]::Round($tokens * ($emitted - $hits) / $emitted, 0)
}

$repoGroups = $prs | Group-Object repo
foreach ($g in $repoGroups) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    Write-Host "=== $($g.Name) ==="

    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt/$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { Write-Warning "  PR#$($pr.pr): worktree failed"; continue }

        # Reading-set ground truth: the files a task genuinely needs in context, which is the editing set (the
        # files the PR changed) plus an optional, hand-labeled reading_set of files an agent must read but not
        # edit (interfaces, callers, tests). Scoring recall against this set, when labeled, measures retrieval
        # against what is needed rather than only what was edited. Absent a reading_set the truth is the editing
        # set, so the current corpus (no labels yet) is unchanged; labeling a PR is deliberate curation that
        # pairs with the larger-corpus rebaseline (B2), since it shifts the published numbers.
        $truth = @($pr.changed_cs)
        if ($pr.PSObject.Properties.Name -contains 'reading_set' -and $pr.reading_set) {
            $truth = @($truth + @($pr.reading_set)) | Where-Object { $_ } | Select-Object -Unique
        }

        # Seed for focus: a non-test changed file's base name.
        $srcChanged = @($truth | Where-Object { $_ -notmatch '(?i)test' })
        $seedFile = if ($srcChanged.Count) { $srcChanged[0] } else { $truth[0] }
        $seed = [System.IO.Path]::GetFileNameWithoutExtension($seedFile)

        # Query: PR title, or (if a merge-noise subject) the changed type names.
        # B7: a merge-noise title carries no task vocabulary (the query falls back to type names), so it is an
        # adversarial case for query mode. Tag it so recall can be reported with and without these PRs.
        $q = $pr.title
        $adversarial = $false
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
            $adversarial = $true
        }

        $modes = @(
            @{ name = 'changes'; flags = @('--changed-since', $pr.base) }
            @{ name = 'focus';   flags = @('--focus', $seed, '--depth', "$Depth") }
            @{ name = 'query';   flags = @('--query', $q, '--query-top', '10', '--depth', "$Depth") }
        )

        foreach ($mode in $modes) {
            foreach ($b in $Budgets) {
                $outDir = Join-Path $ResultsDir ".scope/$($g.Name)_$($pr.pr)/$($mode.name)_$b"
                if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
                New-Item -ItemType Directory -Force -Path $outDir | Out-Null
                $a = @('dotnet','--directory', $wt, '--output', $outDir, '--overwrite',
                    '--format','xml','--tokenizer','o200k_base','--no-cache',
                    '--max-tokens', "$b", '--no-manifest') + $mode.flags
                $null = Measure-Process $Fuse $a
                $emitted = Get-EmittedPaths $outDir
                $r = Measure-Recall $emitted $truth
                $outFile = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Select-Object -First 1
                $tok = if ($outFile) { Get-Tokens $outFile.FullName } else { 0 }
                $results += [pscustomobject]@{
                    repo=$g.Name; pr=$pr.pr; mode=$mode.name; budget=$b; truth=$truth.Count
                    recall=$r.recall; precision=$r.precision; hits=$r.hits; emitted=$r.emitted; tokens=$tok
                    wasted=(Get-WastedTokens $tok $r.emitted $r.hits); stratum=(Get-Stratum $truth.Count)
                    split=(Get-Split $pr.pr); adversarial=$adversarial
                }
            }
        }

        # Grep baseline: rank .cs by query-term hit count, fill the budget.
        $terms = @($q.ToLower() -split '[^a-z0-9]+' | Where-Object { $_.Length -ge 3 -and $stop -notcontains $_ } | Select-Object -Unique)
        $csFiles = Get-CsFiles $wt
        $scored = @()
        foreach ($f in $csFiles) {
            $content = [System.IO.File]::ReadAllText($f.FullName).ToLower()
            $score = 0
            foreach ($t in $terms) { $score += ([regex]::Matches($content, [regex]::Escape($t))).Count }
            if ($score -gt 0) { $scored += [pscustomobject]@{ file=$f; score=$score } }
        }
        $scored = @($scored | Sort-Object score -Descending | Select-Object -First 150)
        $sel = @(); $cum = 0
        if ($scored.Count) {
            $toks = & $TokenCount (@($scored | ForEach-Object { $_.file.FullName })) | ConvertFrom-Json
            $tokMap = @{}
            foreach ($e in $toks.files) { $tokMap[$e.path] = [int]$e.tokens }
            foreach ($s in $scored) {
                $t = $tokMap[$s.file.FullName]
                if ($null -eq $t) { continue }
                if (($cum + $t) -gt $Budget) { continue }
                $cum += $t
                $rel = $s.file.FullName.Substring((Resolve-Path $wt).Path.Length).TrimStart('\','/') -replace '\\','/'
                $sel += $rel
            }
        }
        $gr = Measure-Recall $sel $truth
        $results += [pscustomobject]@{
            repo=$g.Name; pr=$pr.pr; mode='grep'; budget=$Budget; truth=$truth.Count
            recall=$gr.recall; precision=$gr.precision; hits=$gr.hits; emitted=$gr.emitted; tokens=$cum
            wasted=(Get-WastedTokens $cum $gr.emitted $gr.hits); stratum=(Get-Stratum $truth.Count)
            split=(Get-Split $pr.pr); adversarial=$adversarial
        }

        # Console summary reports the headline budget only.
        Write-Host ("  PR#{0,-5} truth {1,2}  changes {2:P0}  focus {3:P0}  query {4:P0}  grep {5:P0}" -f `
            $pr.pr, $truth.Count,
            ($results | Where-Object { $_.pr -eq $pr.pr -and $_.mode -eq 'changes' -and $_.budget -eq $Budget }).recall,
            ($results | Where-Object { $_.pr -eq $pr.pr -and $_.mode -eq 'focus' -and $_.budget -eq $Budget }).recall,
            ($results | Where-Object { $_.pr -eq $pr.pr -and $_.mode -eq 'query' -and $_.budget -eq $Budget }).recall,
            $gr.recall)

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultsDir 'layer2a.json')

# Aggregate mean recall/precision per (mode, budget) so recall@budget is visible across the curve. Also report
# mean wasted tokens (B8) and a cost-adjusted recall = recall x precision (B11), which punishes a mode that
# buys recall by emitting a wide, low-precision set.
$agg = $results | Group-Object mode, budget | ForEach-Object {
    $meanRecall = [math]::Round(($_.Group | Measure-Object recall -Average).Average, 3)
    $meanPrecision = [math]::Round(($_.Group | Measure-Object precision -Average).Average, 3)
    [pscustomobject]@{
        mode = $_.Group[0].mode
        budget = $_.Group[0].budget
        mean_recall = $meanRecall
        mean_precision = $meanPrecision
        mean_tokens = [math]::Round(($_.Group | Measure-Object tokens -Average).Average, 0)
        mean_wasted = [math]::Round(($_.Group | Measure-Object wasted -Average).Average, 0)
        cost_adjusted = [math]::Round($meanRecall * $meanPrecision, 3)
        n = $_.Count
    }
}
$md = @('# Layer 2A results (scoping recall@budget)','',
        "Budgets: $($Budgets -join ', '). Headline budget: $Budget. Focus/query depth: $Depth. PRs: $($prs.Count).",'',
        'Wasted tokens (B8) is the proportional estimate of budget spent on emitted files outside the truth set.',
        'Cost-adjusted recall (B11) is mean recall times mean precision.','',
        '| Mode | Budget | Mean recall | Mean precision | Mean tokens | Wasted tokens | Recall x precision | N |',
        '|------|-------:|------------:|---------------:|------------:|--------------:|-------------------:|--:|')
foreach ($a in ($agg | Sort-Object mode, budget)) {
    $md += ('| {0} | {1} | {2:P0} | {3:P0} | {4} | {5} | {6:P0} | {7} |' -f `
        $a.mode, $a.budget, $a.mean_recall, $a.mean_precision, $a.mean_tokens, $a.mean_wasted, $a.cost_adjusted, $a.n)
}

# B10 recall by change-set size at the headline budget: the large stratum is where the token budget truncates
# truth files, which the overall mean hides.
$modeOrder = @('changes','focus','query','grep')
$strataOrder = @('small (1-3)', 'medium (4-9)', 'large (10+)')
$strata = $results | Where-Object { $_.budget -eq $Budget } | Group-Object mode, stratum | ForEach-Object {
    [pscustomobject]@{
        mode = $_.Group[0].mode
        stratum = $_.Group[0].stratum
        mean_recall = [math]::Round(($_.Group | Measure-Object recall -Average).Average, 3)
        n = $_.Count
    }
}
$md += @('', "## Recall by change-set size (headline budget $Budget)", '',
         ('| Mode | ' + (($strataOrder | ForEach-Object { $_ }) -join ' | ') + ' |'),
         ('|------|' + (($strataOrder | ForEach-Object { '-----:' }) -join '|') + '|'))
foreach ($m in $modeOrder) {
    $cells = foreach ($s in $strataOrder) {
        $row = $strata | Where-Object { $_.mode -eq $m -and $_.stratum -eq $s } | Select-Object -First 1
        if ($row) { '{0:P0} (n={1})' -f $row.mean_recall, $row.n } else { 'n/a' }
    }
    $md += ('| {0} | {1} |' -f $m, ($cells -join ' | '))
}

# B5 held-out dev/test split at the headline budget: mean recall per mode per fold. Tune only on the dev fold
# and report only the test fold, so a tuning gain is not measured on the data it was fit to.
$splitOrder = @('dev', 'test')
$splitRecall = $results | Where-Object { $_.budget -eq $Budget } | Group-Object mode, split | ForEach-Object {
    [pscustomobject]@{
        mode = $_.Group[0].mode
        split = $_.Group[0].split
        mean_recall = [math]::Round(($_.Group | Measure-Object recall -Average).Average, 3)
        n = $_.Count
    }
}
$md += @('', "## Recall by held-out split (headline budget $Budget)", '',
         'Split by PR-id parity (fixed). Tune on dev; publish test.', '',
         ('| Mode | ' + (($splitOrder | ForEach-Object { $_ }) -join ' | ') + ' |'),
         ('|------|' + (($splitOrder | ForEach-Object { '-----:' }) -join '|') + '|'))
foreach ($m in $modeOrder) {
    $cells = foreach ($s in $splitOrder) {
        $row = $splitRecall | Where-Object { $_.mode -eq $m -and $_.split -eq $s } | Select-Object -First 1
        if ($row) { '{0:P0} (n={1})' -f $row.mean_recall, $row.n } else { 'n/a' }
    }
    $md += ('| {0} | {1} |' -f $m, ($cells -join ' | '))
}

# B7 adversarial-case reporting at the headline budget: report query-mode recall with all PRs, with only the
# adversarial (merge-noise) titles, and with them excluded, so the merge-noise cases are never dropped silently
# and the honest "clean title" number is visible alongside the all-in mean.
$advAll  = $results | Where-Object { $_.budget -eq $Budget -and $_.mode -eq 'query' }
$advOnly = $advAll | Where-Object { $_.adversarial }
$advNon  = $advAll | Where-Object { -not $_.adversarial }
function Format-MeanRecall($rows) {
    if (-not $rows -or $rows.Count -eq 0) { return 'n/a' }
    '{0:P0} (n={1})' -f ([math]::Round(($rows | Measure-Object recall -Average).Average, 3)), $rows.Count
}
$md += @('', "## Adversarial-case reporting (B7, query mode, headline budget $Budget)", '',
         'Merge-noise titles carry no task vocabulary, so they are an adversarial case for query mode. Reported',
         'with and without them, never dropped silently.', '',
         '| Set | Mean recall |',
         '|-----|------------:|',
         ('| all PRs | {0} |' -f (Format-MeanRecall $advAll)),
         ('| adversarial only (merge-noise titles) | {0} |' -f (Format-MeanRecall $advOnly)),
         ('| excluding adversarial | {0} |' -f (Format-MeanRecall $advNon)))

# Per-repo mean recall at the headline budget, so the per-repo table (which the aggregate hides) is visible in
# the committed results, not just on the benchmarks page. Modes are columns; repos are rows.
$repoRecall = $results | Where-Object { $_.budget -eq $Budget } | Group-Object repo, mode | ForEach-Object {
    [pscustomobject]@{
        repo = $_.Group[0].repo
        mode = $_.Group[0].mode
        recall = [math]::Round(($_.Group | Measure-Object recall -Average).Average, 3)
    }
}
$md += @('', "## Per repo (headline budget $Budget)", '',
         ('| Repo | ' + (($modeOrder | ForEach-Object { $_ }) -join ' | ') + ' |'),
         ('|------|' + (($modeOrder | ForEach-Object { '-----:' }) -join '|') + '|'))
foreach ($repo in ($repoRecall.repo | Select-Object -Unique | Sort-Object)) {
    $cells = foreach ($m in $modeOrder) {
        $row = $repoRecall | Where-Object { $_.repo -eq $repo -and $_.mode -eq $m } | Select-Object -First 1
        if ($row) { '{0:P0}' -f $row.recall } else { 'n/a' }
    }
    $md += ('| {0} | {1} |' -f $repo, ($cells -join ' | '))
}
$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer2a.md')
$agg | Format-Table | Out-String | Write-Host
Write-Host "Layer 2A complete -> results/layer2a.{json,md}"
