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

        $truth = @($pr.changed_cs)

        # Seed for focus: a non-test changed file's base name.
        $srcChanged = @($truth | Where-Object { $_ -notmatch '(?i)test' })
        $seedFile = if ($srcChanged.Count) { $srcChanged[0] } else { $truth[0] }
        $seed = [System.IO.Path]::GetFileNameWithoutExtension($seedFile)

        # Query: PR title, or (if a merge-noise subject) the changed type names.
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
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

# Aggregate mean recall/precision per (mode, budget) so recall@budget is visible across the curve.
$agg = $results | Group-Object mode, budget | ForEach-Object {
    [pscustomobject]@{
        mode = $_.Group[0].mode
        budget = $_.Group[0].budget
        mean_recall = [math]::Round(($_.Group | Measure-Object recall -Average).Average, 3)
        mean_precision = [math]::Round(($_.Group | Measure-Object precision -Average).Average, 3)
        mean_tokens = [math]::Round(($_.Group | Measure-Object tokens -Average).Average, 0)
        n = $_.Count
    }
}
$md = @('# Layer 2A results (scoping recall@budget)','',
        "Budgets: $($Budgets -join ', '). Headline budget: $Budget. Focus/query depth: $Depth. PRs: $($prs.Count).",'',
        '| Mode | Budget | Mean recall | Mean precision | Mean tokens | N |',
        '|------|-------:|------------:|---------------:|------------:|--:|')
foreach ($a in ($agg | Sort-Object mode, budget)) {
    $md += ('| {0} | {1} | {2:P0} | {3:P0} | {4} | {5} |' -f $a.mode, $a.budget, $a.mean_recall, $a.mean_precision, $a.mean_tokens, $a.n)
}

# Per-repo mean recall at the headline budget, so the per-repo table (which the aggregate hides) is visible in
# the committed results, not just on the benchmarks page. Modes are columns; repos are rows.
$modeOrder = @('changes','focus','query','grep')
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
