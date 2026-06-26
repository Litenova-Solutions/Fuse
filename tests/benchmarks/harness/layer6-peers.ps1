# Layer 6: peer scoper comparison. The static, single-call counterpart to the agentic Layer 5. Give
# each tool a query (the PR title) and score the file set it returns against the same Layer 2A ground
# truth (prs.json changed_cs), so the differentiator (graph quality, dependency-aware scoping) is
# actually tested against a tool that does the same job, not only against a full-dump packer.
#
# FRAMING (mandatory): on .NET, Fuse (Roslyn, C#-specialized) against an offline graph tool
# (CodeGraph, tree-sitter) and an offline .NET lexical tool (coa-codesearch-mcp, Lucene). Fuse should
# have a structural-accuracy home-field edge on C#; we state that and report honestly wherever a peer
# ties or wins. Every peer's published numbers are IGNORED; we report only what this harness measures.
#
# OFFLINE / LOCAL-ONLY peer rule: no API key, no remote model, no server. CodeGraph and coa-codesearch
# both satisfy it. The coa arm's single search call is driven through the same headless `claude -p`
# mechanism Layer 5 validated (coa is MCP-only, no CLI), restricted to its one text_search tool; that
# driver call is mechanical (one tool call), not an agent loop.
#
# This layer is TOOL-DEPENDENT but offline. It is gated off the default run-all path. CodeGraph's
# explore is largely deterministic; the coa arm uses a model to issue one tool call, so when present it
# carries the same model-pinned note as Layer 5.
#
# ARMS:
#   fuse      : fuse --query "<title>" --max-tokens <budget> (one scoped call). Reference.
#   codegraph : codegraph init (setup, outside the measure) then codegraph explore "<title>"
#               --max-files <N> (one call). Map returned symbols/source headers to a file set.
#   coa       : coa-codesearch text_search over a built Lucene index (one call), via headless claude
#               restricted to its search tool. Env-gated on $env:COA_CODESEARCH_EXE (the built
#               COA.CodeSearch.McpServer host); omitted with a notice when unset (omit, never stub).
#
# Usage:
#   pwsh -File tests/benchmarks/harness/layer6-peers.ps1                 # default sample, available peers
#   pwsh -File tests/benchmarks/harness/layer6-peers.ps1 -PerRepo 3 -Full
#   $env:COA_CODESEARCH_EXE = 'C:/path/COA.CodeSearch.McpServer.exe'; pwsh -File layer6-peers.ps1

param(
    [int]$PerRepo = 2,
    [switch]$Full,
    [string[]]$Repos,
    [string[]]$Arms,
    [int]$Budget = 50000,
    [int]$CodegraphMaxFiles = 15,
    # Pinned model for the coa driver call (coa is MCP-only).
    [string]$Model = 'claude-haiku-4-5-20251001'
)

. "$PSScriptRoot/common.ps1"

# Bound every MCP tool call so a wedged coa/Lucene call cannot stall the layer (the claude CLI default
# MCP_TOOL_TIMEOUT is ~28h). The coa arm launches through Invoke-ClaudeBounded (common.ps1), which adds a
# wall-clock backstop and a process-tree kill so no coa MCP server orphans and holds an index lock.
$env:MCP_TOOL_TIMEOUT = '120000'
$env:MCP_TIMEOUT = '30000'
$CoaRolloutTimeoutSec = 300

# --- Peer availability (omit, never stub) ---
$haveFuse      = Test-Path $Fuse
$haveCodegraph = [bool](Get-Command codegraph -ErrorAction SilentlyContinue)
$coaExe        = $env:COA_CODESEARCH_EXE
$haveCoa       = $coaExe -and (Test-Path $coaExe) -and (Get-Command claude -ErrorAction SilentlyContinue)

$allArms = @()
if ($haveFuse)      { $allArms += 'fuse' }      else { Write-Warning "fuse.exe not built; fuse arm omitted." }
if ($haveCodegraph) { $allArms += 'codegraph' } else { Write-Warning "codegraph not installed; codegraph arm omitted." }
if ($haveCoa)       { $allArms += 'coa' }       else { Write-Warning "coa-codesearch not configured (set COA_CODESEARCH_EXE); coa arm omitted." }
if (-not $allArms) { Write-Warning "No peers available; nothing to run."; return }

if ($Repos) { $Repos = @($Repos | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
if ($Arms)  { $Arms  = @($Arms  | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$armsToRun = if ($Arms) { @($Arms | Where-Object { $allArms -contains $_ }) } else { $allArms }
Write-Host "Layer 6 arms: $($armsToRun -join ', ')  (budget $Budget tokens)" -ForegroundColor Cyan

# --- Sample (deterministic, meaningful, logged) ---
$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json
if ($Repos) { $prs = $prs | Where-Object { $Repos -contains $_.repo } }
$sampled = @()
foreach ($grp in ($prs | Group-Object repo)) {
    if ($Full) { $sampled += $grp.Group; continue }
    $eligible = @($grp.Group | Where-Object { @($_.changed_cs).Count -ge 1 -and $_.title -notmatch '(?i)^merge ' })
    $take = $eligible | Select-Object -First $PerRepo
    if (-not $take) { $take = $grp.Group | Select-Object -First $PerRepo }
    $sampled += $take
}
Write-Host "Sampled PRs ($($sampled.Count)): $((@($sampled | ForEach-Object { "$($_.repo)#$($_.pr)" })) -join ', ')" -ForegroundColor Cyan

function Get-TokensOf([string]$path) { if (Test-Path $path) { return (Get-Tokens $path) } else { return 0 } }

# Token count over many files at once, piping paths via stdin (same approach as layer4's Get-TokensMulti).
function Get-TokensMulti6($paths) {
    $list = @($paths)
    if ($list.Count -eq 0) { return 0 }
    $json = ($list -join "`n") | & $TokenCount --stdin-list | ConvertFrom-Json
    return [int]$json.total
}

# Score a returned file set against the truth.
function Score-Set($acquired, $truth) {
    $acq = @($acquired | ForEach-Object { $_ -replace '\\','/' } | Sort-Object -Unique)
    $tr = @($truth | ForEach-Object { $_ -replace '\\','/' })
    $hit = @($tr | Where-Object { $acq -contains $_ })
    $recall = if ($tr.Count) { [math]::Round($hit.Count / $tr.Count, 3) } else { 0 }
    $prec = if ($acq.Count) { [math]::Round($hit.Count / $acq.Count, 3) } else { 0 }
    return @{ recall = $recall; precision = $prec; acquired = $acq.Count; hits = $hit.Count }
}

function Get-FuseFiles($outDir) {
    @(Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue) | ForEach-Object {
        $t = [System.IO.File]::ReadAllText($_.FullName)
        [regex]::Matches($t, '<file path="([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\','/' }
    }
}

$rows = @()
foreach ($grp in ($sampled | Group-Object repo)) {
    $repoEntry = Get-Corpus | Where-Object { $_.name -eq $grp.Name }
    $repoPath = Resolve-RepoPath $repoEntry
    foreach ($prItem in $grp.Group) {
        $tag = "$($grp.Name)_$($prItem.pr)"
        Write-Host "`n=== $($grp.Name)#$($prItem.pr): $($prItem.title) ===" -ForegroundColor Yellow
        $wt = Join-Path $ResultsDir ".wt6/$tag"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree prune 2>$null
        git -C $repoPath worktree add --detach --force $wt $prItem.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { Write-Warning "  worktree failed; skipping"; continue }
        $wtFull = (Resolve-Path $wt).Path
        $truth = @($prItem.changed_cs)
        $q = $prItem.title
        if ($q -match '(?i)^merge ') {
            $q = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }

        foreach ($arm in $armsToRun) {
            $files = @(); $tokens = 0; $setupMs = $null
            try {
                switch ($arm) {
                    'fuse' {
                        # V3 surface: build the persistent index once, then localize the title to ranked
                        # candidate file paths (no bodies). Token cost is the candidate files a consumer reads.
                        $null = & $Fuse index $wt *> $null
                        $locOut = (& $Fuse localize $wt --task $q --max-candidates 10 2>&1 | Out-String)
                        $set = New-Object System.Collections.Generic.HashSet[string]
                        foreach ($m in [regex]::Matches($locOut, '(?m)^\s*\d+\.\d+\s+(\S+\.cs)\b')) {
                            [void]$set.Add(($m.Groups[1].Value -replace '\\','/'))
                        }
                        $files = @($set)
                        $abs = @($files | ForEach-Object { Join-Path $wt $_ } | Where-Object { Test-Path $_ })
                        $tokens = (Get-TokensMulti6 $abs)
                        if (Test-Path (Join-Path $wt '.fuse')) { Remove-Item -Recurse -Force (Join-Path $wt '.fuse') -ErrorAction SilentlyContinue }
                    }
                    'codegraph' {
                        $sw = [System.Diagnostics.Stopwatch]::StartNew()
                        Push-Location $wtFull
                        try { & codegraph init --force *> $null } finally { Pop-Location }
                        $sw.Stop(); $setupMs = $sw.ElapsedMilliseconds
                        $cgOut = Join-Path $ResultsDir ".out6/$tag.codegraph.txt"
                        New-Item -ItemType Directory -Force -Path (Split-Path $cgOut) | Out-Null
                        Push-Location $wtFull
                        try { & codegraph explore $q --max-files $CodegraphMaxFiles *> $cgOut } finally { Pop-Location }
                        $s = if (Test-Path $cgOut) { [System.IO.File]::ReadAllText($cgOut) } else { '' }
                        $set = New-Object System.Collections.Generic.HashSet[string]
                        foreach ($m in [regex]::Matches($s, '\*\*`([^`]+\.cs)`\*\*')) { [void]$set.Add(($m.Groups[1].Value -replace '\\','/')) }
                        foreach ($m in [regex]::Matches($s, '\(([A-Za-z0-9_./\\-]+\.cs):\d+\)')) { [void]$set.Add(($m.Groups[1].Value -replace '\\','/')) }
                        $files = @($set)
                        $tokens = (Get-TokensOf $cgOut)
                        if (Test-Path (Join-Path $wtFull '.codegraph')) { Remove-Item -Recurse -Force (Join-Path $wtFull '.codegraph') -ErrorAction SilentlyContinue }
                    }
                    'coa' {
                        # coa is MCP-only: drive one text_search via headless claude restricted to its tools.
                        $cfg = Join-Path $ResultsDir ".out6/coa.mcp.json"
                        @{ mcpServers = @{ coa = @{ command = $coaExe; args = @() } } } | ConvertTo-Json -Depth 6 | Set-Content $cfg
                        $coaStream = Join-Path $ResultsDir ".out6/$tag.coa.jsonl"
                        $prompt = "Index this workspace, then search it for code relevant to: $q. Return the repo-relative paths of the most relevant files as a bullet list."
                        # Bounded launch (worktree as cwd): a hung coa MCP call is cut by MCP_TOOL_TIMEOUT, and a
                        # wedged claude is cut by the wall-clock backstop, which kills the tree so no coa server orphans.
                        $coaArgv = @('-p', $prompt, '--model', $Model, '--output-format','stream-json','--verbose',
                                     '--permission-mode','default','--max-turns','8','--allowedTools','mcp__coa',
                                     '--mcp-config', $cfg, '--strict-mcp-config')
                        $null = Invoke-ClaudeBounded $coaArgv $coaStream $wtFull $CoaRolloutTimeoutSec
                        $set = New-Object System.Collections.Generic.HashSet[string]
                        $accum = ''
                        foreach ($ln in (Get-Content $coaStream -ErrorAction SilentlyContinue)) {
                            if (-not $ln.Trim()) { continue }
                            try { $o = $ln | ConvertFrom-Json } catch { continue }
                            if ($o.type -eq 'user' -and $o.message.content) {
                                foreach ($c in $o.message.content) {
                                    if ($c.type -eq 'tool_result') {
                                        $t = if ($c.content -is [string]) { $c.content } else { ($c.content | ForEach-Object { $_.text }) -join "`n" }
                                        $accum += "`n$t"
                                    }
                                }
                            }
                        }
                        foreach ($m in [regex]::Matches([string]$accum, '([A-Za-z0-9_./\\-]+\.cs)')) { [void]$set.Add(($m.Groups[1].Value -replace '\\','/' -replace "^$([regex]::Escape(($wtFull -replace '\\','/')))/?", '')) }
                        $files = @($set)
                        # Token cost of what coa returned to the agent (its search-result text). TokenCount only
                        # counts files, so write the accumulated tool-result text to a temp file and count that.
                        # Note: coa returns a ranked path/snippet list, not full reduced source, so this token
                        # number is not directly comparable to the fuse arm's reduced-code payload; recall and
                        # precision are the comparable axes (see the MD footer).
                        $coaTxt = Join-Path $ResultsDir ".out6/$tag.coa.result.txt"
                        [string]$accum | Set-Content $coaTxt
                        $tokens = (Get-TokensOf $coaTxt)
                    }
                }
            } catch { Write-Warning "  $arm failed: $_" }

            # Normalize any absolute paths to repo-relative.
            $wtNorm = ($wtFull -replace '\\','/')
            $files = @($files | ForEach-Object { $p = ($_ -replace '\\','/'); if ($p -like "$wtNorm/*") { $p.Substring($wtNorm.Length+1) } else { $p } })
            $sc = Score-Set $files $truth
            $rows += [pscustomobject]@{
                repo = $grp.Name; pr = $prItem.pr; arm = $arm; budget = $Budget
                truth = @($truth).Count; acquired = $sc.acquired; hits = $sc.hits
                recall = $sc.recall; precision = $sc.precision; tokens = $tokens; setup_ms = $setupMs
            }
            Write-Host ("  {0,-9}: recall {1:P0} prec {2:P0} {3,7} tok ({4} files)" -f $arm, $sc.recall, $sc.precision, $tokens, $sc.acquired)
        }

        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

if (-not $rows) { Write-Warning "No rows produced."; return }
$rows | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDir 'layer6-peers.json')
$rows | Export-Csv -NoTypeInformation -Path (Join-Path $ResultsDir 'layer6-peers.csv')

$md = @()
$md += '# Layer 6 results (peer scoper comparison)'
$md += ''
$md += 'On .NET: Fuse (Roslyn, C#-specialized) against an offline graph tool (CodeGraph, tree-sitter) and,'
$md += 'when configured, an offline .NET lexical tool (coa-codesearch-mcp, Lucene). Each tool gets the PR'
$md += 'title as the query and returns a file set, scored against the Layer 2A ground truth (changed_cs).'
$md += 'Recall is read together with tokens. Peer published numbers are ignored; only harness-measured'
$md += 'figures appear here. CodeGraph index build is setup, excluded from the reported token cost.'
$md += ''
$md += "- Budget: $Budget tokens (fuse --max-tokens; codegraph --max-files $CodegraphMaxFiles)."
$md += "- Arms: $($armsToRun -join ', ')."
$md += "- PRs sampled ($($sampled.Count)): $((@($sampled | ForEach-Object { "$($_.repo)#$($_.pr)" })) -join ', ')."
if ($armsToRun -contains 'coa') { $md += "- coa arm is model-driven (one text_search call via $Model); tool-dependent and not byte-reproducible." }
$md += ''
$md += '## Aggregate (mean over sampled PRs)'
$md += ''
$md += '| Arm | Mean recall | Mean precision | Mean tokens |'
$md += '|-----|------------:|---------------:|------------:|'
foreach ($arm in $armsToRun) {
    $a = @($rows | Where-Object { $_.arm -eq $arm })
    if (-not $a) { continue }
    $rec = [math]::Round(($a | Measure-Object recall -Average).Average, 3)
    $prec = [math]::Round(($a | Measure-Object precision -Average).Average, 3)
    $tk = [math]::Round(($a | Measure-Object tokens -Average).Average, 0)
    $md += ('| {0} | {1:P0} | {2:P0} | {3:N0} |' -f $arm, $rec, $prec, $tk)
}
$md += ''
$md += '## Per repo (mean recall, mean tokens)'
$md += ''
$md += '| Repo | ' + (($armsToRun | ForEach-Object { "$_ recall" }) -join ' | ') + ' |'
$md += '|------|' + (($armsToRun | ForEach-Object { '------:' }) -join '|') + '|'
foreach ($repoName in (@($rows | Select-Object -ExpandProperty repo -Unique | Sort-Object))) {
    $cells = foreach ($arm in $armsToRun) {
        $a = @($rows | Where-Object { $_.repo -eq $repoName -and $_.arm -eq $arm })
        if ($a) { '{0:P0}' -f [math]::Round(($a | Measure-Object recall -Average).Average, 3) } else { 'n/a' }
    }
    $md += ('| {0} | {1} |' -f $repoName, ($cells -join ' | '))
}
$md += ''
$md += '## How to read this'
$md += ''
$md += 'Recall (and precision) of the returned file set against the change set is the comparable axis across'
$md += 'all three arms. The token columns are NOT directly comparable: fuse returns the reduced source of the'
$md += 'scoped set (a payload the agent can read directly), codegraph explore also returns verbatim source,'
$md += 'but coa returns a ranked path/snippet list (pointers the agent would still have to open), so its token'
$md += 'count is far smaller by construction and does not represent delivered context. The coa arm is also'
$md += 'model-driven (a sonnet-4-6 driver issues one search and reports paths), so its recall blends the tool'
$md += 'with the driver. Fuse has a Roslyn home-field edge on C# structure; where a peer ties or beats it, that'
$md += 'is reported as-is. Sample is small and per-repo rows are shown so a single arm-vs-arm point is not over-read.'

$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer6-peers.md')
Write-Host "`nLayer 6 complete -> results/layer6-peers.{json,csv,md}  ($($rows.Count) rows)" -ForegroundColor Green
