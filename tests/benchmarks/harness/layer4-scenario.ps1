# Layer 4: context-acquisition scenario. Puts three approaches on the two honest axes
# (round-trips and input tokens) for the same real task, using the PR change sets as ground
# truth. The task is "assemble the context this change needs"; the needed files are changed_cs.
#
# Arms, per PR, at budgets 10000/25000/50000 (headline 50000):
#   no-fuse : the agent reads the files the task needs, one at a time.
#             input_tokens = exact tokens of changed_cs read in full.
#             round_trips  = count of changed_cs. This is a STRUCTURAL LOWER BOUND: a blind
#                            agent must read each needed file at least once and in practice
#                            reads more while exploring. Labeled round_trips_is_lower_bound.
#             recall       = 1.0 by construction (it is exactly the needed set).
#             also records no_fuse_whole_repo_tokens = exact tokens of every .cs file in the
#                            repo, the "explore blind" ceiling an agent pays if it cannot find
#                            the right files.
#   repomix : the cost-of-not-scoping baseline. A generic full-dump packer that never scopes:
#             one dump of the whole repo at the PR head. input_tokens = tokens of the dump.
#             round_trips = 1. recall = 1.0 by construction (it contains everything), so its role
#             is to show what blind packing costs in tokens, not to contest scoping (it cannot lose
#             on scoping because it never scopes). Omitted for a PR when Repomix is unavailable
#             (no npx / sub-floor stub), never stubbed.
#   fuse    : one scoped call, fuse --query "<PR title>" --depth 2 --level standard --max-tokens
#             <budget>. input_tokens = exact tokens of the emitted output. round_trips = 1.
#             recall = fraction of changed_cs present in the output.
#
# All token counts use o200k_base. Always read Fuse's token number together with its recall:
# a low token count that dropped needed files is not a win. no-fuse and repomix have recall 1.0
# by construction, so the contest is tokens at one call (vs repomix) and round-trips vs blind
# exploration (vs no-fuse), not a recall contest.
#
# Output: results/layer4-scenario.json, .csv, .md

. "$PSScriptRoot/common.ps1"

$Budgets  = @(10000, 25000, 50000)
$Budget   = 50000   # headline budget
$Depth    = 2
$RepomixTokenFloor = 1000

$haveNpx = [bool](Get-Command npx -ErrorAction SilentlyContinue)
if (-not $haveNpx) { Write-Warning "npx not found; the Repomix arm will be omitted for every PR." }

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
    return $recall
}

# Token count over many files at once, piping paths via stdin so the whole-repo set (hundreds of
# files) does not overflow the command line.
function Get-TokensMulti($paths) {
    $list = @($paths)
    if ($list.Count -eq 0) { return 0 }
    $json = ($list -join "`n") | & $TokenCount --stdin-list | ConvertFrom-Json
    return [int]$json.total
}

# Run one Fuse invocation (the argv after the exe) into a fresh output directory and return its emitted
# token count and recall against the truth set. Shared by the query, changes, and ask arms.
function Invoke-FuseArm([string[]]$argv, $outDir, $truth) {
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $null = Measure-Process $Fuse $argv
    $emitted = Get-EmittedPaths $outDir
    $recall = Measure-Recall $emitted $truth
    $files = @(Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    return @{ tokens = (Get-TokensMulti $files); recall = $recall }
}

$repoGroups = $prs | Group-Object repo
foreach ($g in $repoGroups) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    Write-Host "=== $($g.Name) ==="

    foreach ($pr in $g.Group) {
        $wt = Join-Path $ResultsDir ".wt4/$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { Write-Warning "  PR#$($pr.pr): worktree failed"; continue }
        $wtFull = (Resolve-Path $wt).Path

        $truth = @($pr.changed_cs)

        # no-fuse: relevant-set tokens (files the task needs) and the whole-repo ceiling.
        $relPaths = @()
        foreach ($rel in $truth) {
            $p = Join-Path $wtFull ($rel -replace '/','\')
            if (Test-Path $p) { $relPaths += $p }
        }
        $relTokens   = Get-TokensMulti $relPaths
        $wholeFiles  = @(Get-CsFiles $wt | ForEach-Object { $_.FullName })
        $wholeTokens = Get-TokensMulti $wholeFiles

        # query: PR title, or (if a merge-noise subject) the changed type names.
        $q = $pr.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        # Repomix dump of the head worktree (one dump reused across the budget rows).
        $repomixTokens = $null
        if ($haveNpx) {
            $rmxOut = Join-Path $ResultsDir ".out4/$($g.Name)_$($pr.pr).repomix.xml"
            New-Item -ItemType Directory -Force -Path (Split-Path $rmxOut) | Out-Null
            if (Test-Path $rmxOut) { Remove-Item -Force $rmxOut }
            try {
                # --no-gitignore --no-default-patterns matches the layer-1 invocation, so the generic-packer
                # arm has identical semantics in both layers (dump the C# file set, ignoring repomix's own
                # default exclusions). The worktree is a git checkout, so this is the same set git would track.
                & npx --yes repomix $wt -o $rmxOut --include '**/*.cs' --no-gitignore --no-default-patterns --style xml *> $null
                if (Test-Path $rmxOut) {
                    $rt = Get-Tokens $rmxOut
                    if ($rt -ge $RepomixTokenFloor) { $repomixTokens = $rt }
                    else { Write-Warning "  PR#$($pr.pr): repomix stub ($rt tok); omitting" }
                }
            } catch { Write-Warning "  PR#$($pr.pr): repomix failed; omitting" }
        }

        foreach ($b in $Budgets) {
            # no-fuse arm (budget-independent values; recorded per budget so the curve is uniform).
            $results += [pscustomobject]@{
                repo = $g.Name; pr = $pr.pr; arm = 'no-fuse'; budget = $b; truth = $truth.Count
                round_trips = $truth.Count; round_trips_is_lower_bound = $true
                input_tokens = $relTokens; no_fuse_whole_repo_tokens = $wholeTokens
                recall = 1.0; recall_by_construction = $true
            }

            # repomix arm (budget-independent; omitted when unavailable).
            if ($null -ne $repomixTokens) {
                $results += [pscustomobject]@{
                    repo = $g.Name; pr = $pr.pr; arm = 'repomix'; budget = $b; truth = $truth.Count
                    round_trips = 1; round_trips_is_lower_bound = $false
                    input_tokens = $repomixTokens; no_fuse_whole_repo_tokens = $wholeTokens
                    recall = 1.0; recall_by_construction = $true
                }
            }

            # fuse arm: one scoped --query call at this budget.
            $outDir = Join-Path $ResultsDir ".scope4/$($g.Name)_$($pr.pr)/query_$b"
            if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
            New-Item -ItemType Directory -Force -Path $outDir | Out-Null
            $a = @('dotnet','--directory', $wt, '--output', $outDir, '--overwrite',
                '--format','xml','--tokenizer','o200k_base','--no-cache','--no-manifest',
                '--max-tokens', "$b", '--level','standard',
                '--query', $q, '--query-top','10','--depth', "$Depth")
            $null = Measure-Process $Fuse $a
            $emitted = Get-EmittedPaths $outDir
            $recall = Measure-Recall $emitted $truth
            $fuseFiles = @(Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
            $fuseTokens = Get-TokensMulti $fuseFiles
            $results += [pscustomobject]@{
                repo = $g.Name; pr = $pr.pr; arm = 'fuse'; budget = $b; truth = $truth.Count
                round_trips = 1; round_trips_is_lower_bound = $false
                input_tokens = $fuseTokens; no_fuse_whole_repo_tokens = $wholeTokens
                recall = $recall; recall_by_construction = $false
            }

            # fuse-changes arm: the routed arm when a git base is available. One scoped call seeding on the
            # diff against the PR base, with first-degree dependents. This is the headline routed arm: the
            # plain --query arm above is the stress floor (a sentence, no base, picked search).
            $chRes = Invoke-FuseArm @(
                'dotnet','--directory', $wt, '--output', (Join-Path $ResultsDir ".scope4/$($g.Name)_$($pr.pr)/changes_$b"),
                '--overwrite','--format','xml','--tokenizer','o200k_base','--no-cache','--no-manifest',
                '--max-tokens', "$b", '--level','standard',
                '--changed-since', $pr.base, '--include-dependents'
            ) (Join-Path $ResultsDir ".scope4/$($g.Name)_$($pr.pr)/changes_$b") $truth
            $results += [pscustomobject]@{
                repo = $g.Name; pr = $pr.pr; arm = 'fuse-changes'; budget = $b; truth = $truth.Count
                round_trips = 1; round_trips_is_lower_bound = $false
                input_tokens = $chRes.tokens; no_fuse_whole_repo_tokens = $wholeTokens
                recall = $chRes.recall; recall_by_construction = $false
            }

            # fuse-ask arm: Fuse routes the task to a strategy and packs to budget. For a PR title this is the
            # router's honest choice (search, or focus when the title names a type), one call.
            $askRes = Invoke-FuseArm @(
                'ask','--directory', $wt, '--output', (Join-Path $ResultsDir ".scope4/$($g.Name)_$($pr.pr)/ask_$b"),
                '--overwrite','--format','xml','--tokenizer','o200k_base','--no-cache','--no-manifest',
                '--max-tokens', "$b", '--task', $q
            ) (Join-Path $ResultsDir ".scope4/$($g.Name)_$($pr.pr)/ask_$b") $truth
            $results += [pscustomobject]@{
                repo = $g.Name; pr = $pr.pr; arm = 'fuse-ask'; budget = $b; truth = $truth.Count
                round_trips = 1; round_trips_is_lower_bound = $false
                input_tokens = $askRes.tokens; no_fuse_whole_repo_tokens = $wholeTokens
                recall = $askRes.recall; recall_by_construction = $false
            }
        }

        $rmxShow = if ($null -ne $repomixTokens) { "{0,8}" -f $repomixTokens } else { "  (n/a)" }
        # Filter by repo too: PR numbers collide across repos (e.g. MediatR and NewtonsoftJson both have #1158).
        $fuseHead = ($results | Where-Object { $_.repo -eq $g.Name -and $_.pr -eq $pr.pr -and $_.arm -eq 'fuse' -and $_.budget -eq $Budget })
        Write-Host ("  PR#{0,-5} need {1,2}  no-fuse rel {2,7} whole {3,8}  repomix {4}  fuse {5,7} tok recall {6:P0}" -f `
            $pr.pr, $truth.Count, $relTokens, $wholeTokens, $rmxShow, $fuseHead.input_tokens, $fuseHead.recall)

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultsDir 'layer4-scenario.json')
$results | Export-Csv -NoTypeInformation -Path (Join-Path $ResultsDir 'layer4-scenario.csv')

# Aggregate at the headline budget, means across PRs.
$head = $results | Where-Object { $_.budget -eq $Budget }
$noFuse  = $head | Where-Object { $_.arm -eq 'no-fuse' }
$repomix = $head | Where-Object { $_.arm -eq 'repomix' }
$fuse    = $head | Where-Object { $_.arm -eq 'fuse' }

$meanK        = [math]::Round(($noFuse | Measure-Object round_trips -Average).Average, 1)
$meanRel      = [math]::Round(($noFuse | Measure-Object input_tokens -Average).Average, 0)
$meanWhole    = [math]::Round(($noFuse | Measure-Object no_fuse_whole_repo_tokens -Average).Average, 0)
$meanRepomix  = if ($repomix) { [math]::Round(($repomix | Measure-Object input_tokens -Average).Average, 0) } else { $null }
$meanFuse     = [math]::Round(($fuse | Measure-Object input_tokens -Average).Average, 0)
$meanFuseRec  = [math]::Round(($fuse | Measure-Object recall -Average).Average, 3)

function Fmt($n) { if ($null -eq $n) { 'n/a' } else { '{0:N0}' -f $n } }

$md = @()
$md += '# Layer 4 results (context acquisition: Fuse vs no Fuse vs Repomix)'
$md += ''
$md += "Tokenizer o200k_base. Budgets $($Budgets -join ', '); headline budget $Budget. PRs: $($prs.Count). Fuse query depth $Depth."
$md += 'Means across the PRs at the headline budget. no-fuse round-trips is a structural lower bound (a blind agent reads each needed file at least once, and more while exploring), not a measured agent. Repomix is the cost-of-not-scoping baseline: a generic full-dump packer that never scopes, so its recall is 1.00 by construction and its role is to show what blind packing costs in tokens, not to contest scoping. no-fuse and Repomix both have recall 1.00 by construction.'
$md += ''
$md += '| Arm | Round-trips | Input tokens | Recall of needed files |'
$md += '|-----|------------:|-------------:|-----------------------:|'
$md += ('| no-fuse (blind, whole repo) | >= {0} | {1} | 1.00 |' -f $meanK, (Fmt $meanWhole))
$md += ('| no-fuse (relevant set) | >= {0} | {1} | 1.00 |' -f $meanK, (Fmt $meanRel))
if ($null -ne $meanRepomix) {
    $md += ('| Repomix (one dump) | 1 | {0} | 1.00 |' -f (Fmt $meanRepomix))
} else {
    $md += '| Repomix (one dump) | 1 | (unavailable: no npx) | 1.00 |'
}
$md += ('| Fuse (--query) | 1 | {0} | {1:P0} |' -f (Fmt $meanFuse), $meanFuseRec)
$md += ''
$md += '## Per repo (headline budget)'
$md += ''
$md += '| Repo | no-fuse K (>=) | no-fuse rel tok | whole-repo tok | Repomix tok | Fuse tok | Fuse recall |'
$md += '|------|---------------:|----------------:|---------------:|------------:|---------:|------------:|'
foreach ($name in ($head | Select-Object -ExpandProperty repo -Unique | Sort-Object)) {
    $rNo  = $head | Where-Object { $_.arm -eq 'no-fuse' -and $_.repo -eq $name }
    $rRmx = $head | Where-Object { $_.arm -eq 'repomix' -and $_.repo -eq $name }
    $rFu  = $head | Where-Object { $_.arm -eq 'fuse' -and $_.repo -eq $name }
    $k   = [math]::Round(($rNo | Measure-Object round_trips -Average).Average, 1)
    $rel = [math]::Round(($rNo | Measure-Object input_tokens -Average).Average, 0)
    $wh  = [math]::Round(($rNo | Measure-Object no_fuse_whole_repo_tokens -Average).Average, 0)
    $rm  = if ($rRmx) { [math]::Round(($rRmx | Measure-Object input_tokens -Average).Average, 0) } else { $null }
    $fu  = [math]::Round(($rFu | Measure-Object input_tokens -Average).Average, 0)
    $fr  = [math]::Round(($rFu | Measure-Object recall -Average).Average, 3)
    $md += ('| {0} | {1} | {2} | {3} | {4} | {5} | {6:P0} |' -f $name, $k, (Fmt $rel), (Fmt $wh), (Fmt $rm), (Fmt $fu), $fr)
}
# Routed arms at the headline budget: the change-scoped and ask arms next to the query stress floor.
function Get-ArmMean($arm, $field) {
    $rows = $head | Where-Object { $_.arm -eq $arm }
    if (-not $rows) { return $null }
    return [math]::Round(($rows | Measure-Object $field -Average).Average, ($(if ($field -eq 'recall') { 3 } else { 0 })))
}

$md += ''
$md += '## Routed arms (headline budget)'
$md += ''
$md += 'The change-scoped arm is the routed default when a git base is available; the ask arm is what Fuse picks from the task text; the query arm is the stress floor (a sentence, no base, picked search). All are one call.'
$md += ''
$md += '| Arm | Recall | Mean tokens |'
$md += '|-----|-------:|------------:|'
$md += ('| fuse --changed-since (routed) | {0:P0} | {1} |' -f (Get-ArmMean 'fuse-changes' 'recall'), (Fmt (Get-ArmMean 'fuse-changes' 'input_tokens')))
$md += ('| fuse ask (routed) | {0:P0} | {1} |' -f (Get-ArmMean 'fuse-ask' 'recall'), (Fmt (Get-ArmMean 'fuse-ask' 'input_tokens')))
$md += ('| fuse --query (stress floor) | {0:P0} | {1} |' -f (Get-ArmMean 'fuse' 'recall'), (Fmt (Get-ArmMean 'fuse' 'input_tokens')))

# Tokens to reach a target recall: the smallest budget whose mean recall clears the target, with the mean
# tokens actually spent there. Shows that change scoping reaches high recall at a tighter budget than query.
$target = 0.80
$md += ''
$md += ("Tokens to reach {0:P0} recall (smallest budget whose mean recall clears it):" -f $target)
$md += ''
$md += '| Arm | Budget reached | Mean tokens there |'
$md += '|-----|---------------:|------------------:|'
foreach ($arm in @('fuse-changes','fuse-ask','fuse')) {
    $reached = $null; $tokensThere = $null
    foreach ($b in $Budgets) {
        $rows = $results | Where-Object { $_.arm -eq $arm -and $_.budget -eq $b }
        if (-not $rows) { continue }
        $mr = ($rows | Measure-Object recall -Average).Average
        if ($mr -ge $target) { $reached = $b; $tokensThere = [math]::Round(($rows | Measure-Object input_tokens -Average).Average, 0); break }
    }
    $label = switch ($arm) { 'fuse-changes' { 'fuse --changed-since' } 'fuse-ask' { 'fuse ask' } default { 'fuse --query' } }
    if ($null -ne $reached) {
        $md += ('| {0} | {1} | {2} |' -f $label, $reached, (Fmt $tokensThere))
    } else {
        $md += ('| {0} | not reached at <= {1} | n/a |' -f $label, ($Budgets[-1]))
    }
}

$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer4-scenario.md')

Write-Host ""
Write-Host ("Headline ({0} budget): no-fuse K>={1} rel {2} tok / whole {3} tok | repomix {4} tok | fuse {5} tok at recall {6:P0}" -f `
    $Budget, $meanK, (Fmt $meanRel), (Fmt $meanWhole), (Fmt $meanRepomix), (Fmt $meanFuse), $meanFuseRec)
Write-Host "Layer 4 complete -> results/layer4-scenario.{json,csv,md}"
