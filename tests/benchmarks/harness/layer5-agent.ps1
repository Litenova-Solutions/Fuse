# Layer 5: agent-in-the-loop context sufficiency. The centerpiece. A real agent (the claude CLI in
# headless mode) is given a task and one of four toolboxes; the same model brain drives every arm, so
# any difference is attributable to the tools, not the brain. We measure the cost to acquire sufficient
# context (tool calls, cumulative input tokens) and the quality of what it acquired (recall and
# precision against the PR change set, plus a model-scored sufficiency verdict).
#
# This layer is MODEL-DEPENDENT and NOT byte-reproducible. It is gated off the default run-all path.
# V3 note: the agent suite is reimplemented in C# as Fuse.Benchmarks AgentSuite (run via `fuse eval agent`);
# this PowerShell driver is retained as the reference for the ported logic.
#
# THE FOUR ARMS (one Claude driver, only the toolbox changes):
#   native    : filesystem read tools only (Read, Grep, Glob). No MCP. This is what an agent pays
#               with no scoping tool: it explores file by file, and the cost compounds over turns.
#   fuse      : the fuse_* MCP tools only (plus a minimal Read). One or two scoped calls.
#   codegraph : the CodeGraph MCP tools only (plus a minimal Read). Requires a one-time `codegraph init`
#               per PR-head worktree to build .codegraph/ before the rollouts; that index build is
#               SETUP, outside the measured trajectory (the analogue of Fuse's prebuilt index).
#   serena    : the Serena MCP toolkit only (plus a minimal Read). Serena is multi-call by design
#               (find symbol, find referencing symbols, navigate), so expect more tool calls; that is
#               the point of measuring it. Its LSP warm-up is SETUP, outside the measured trajectory.
#
# HONESTY CONTRACT (model-dependent layer):
#   - Model id and version, run date, and N are pinned in the results header.
#   - We report a DISTRIBUTION (median and IQR) over N rollouts, not a single point estimate.
#   - Recall is always read together with tokens; a low token count that dropped needed files is
#     not a win.
#   - Losses and per-repo variance are shown, never hidden in a mean.
#   - An arm whose tool is not installed is OMITTED with a notice (omit, never stub).
#   - The sufficiency verdict is model-scored and labeled as such; the file-set recall/precision
#     against ground truth is the objective measure.
#
# Usage:
#   pwsh -File tests/benchmarks/harness/layer5-agent.ps1                 # default sample, all available arms
#   pwsh -File tests/benchmarks/harness/layer5-agent.ps1 -Sample 10 -Rollouts 3
#   pwsh -File tests/benchmarks/harness/layer5-agent.ps1 -Arms native,fuse -Full
#   FUSE_BENCH_AGENT=1 pwsh -File run-all.ps1                            # opt-in from the front door (off by default)

param(
    # PRs sampled per repo (deterministic: the first N PRs of each repo, so the subset is fixed and logged).
    [int]$PerRepo = 1,
    # Run every PR instead of the per-repo sample.
    [switch]$Full,
    # Rollouts per (PR, arm). The distribution is read over these.
    [int]$Rollouts = 2,
    # Pinned model id. Headless claude defaults to Haiku; we pin it explicitly so the results name the model.
    [string]$Model = 'claude-haiku-4-5-20251001',
    # Which arms to run; default = every arm whose tool is installed.
    [string[]]$Arms,
    # Restrict to these repos (by corpus name); default = all repos in the sample.
    [string[]]$Repos,
    # Cap turns per rollout so a runaway native exploration cannot blow the cost budget.
    [int]$MaxTurns = 25,
    # Per MCP tool-call timeout (seconds). The claude CLI default for MCP_TOOL_TIMEOUT is ~28 hours, so a
    # wedged or merely slow MCP tool call (a stuck `fuse mcp serve`, codegraph, or serena) would block the
    # whole layer effectively forever instead of failing fast. This bounds EVERY single MCP tool call; a
    # call that blows it errors out and the rollout is omitted (never stubbed). Cold index builds in this
    # corpus finish in a few seconds, so 120s is generous headroom while still bounding a true hang.
    [int]$McpToolTimeoutSec = 120,
    # Wall-clock backstop for an ENTIRE rollout (seconds). If the claude process itself wedges (not just one
    # MCP call), the whole process tree is killed and the rollout is omitted. This also closes the orphaned-
    # server leak: a bare interrupt would otherwise leave `fuse mcp serve` children alive, and live orphans
    # hold the worktree cache lock so the next run blocks on it.
    [int]$RolloutTimeoutSec = 600
)

. "$PSScriptRoot/common.ps1"

# Bound every MCP tool call and server startup at the CLI level so a wedged server cannot stall the layer.
# Units are milliseconds. MCP_TOOL_TIMEOUT overrides the CLI's ~28h default; MCP_TIMEOUT bounds startup.
# Start-Process below inherits this process environment, so setting it here covers every claude launch.
$env:MCP_TOOL_TIMEOUT = "$($McpToolTimeoutSec * 1000)"
$env:MCP_TIMEOUT = '30000'
# The bounded claude launcher (Invoke-ClaudeBounded) is defined in common.ps1 and shared with the spike.

# --- Tool availability (omit, never stub) ---
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Warning "claude CLI not found; Layer 5 (agent) requires it. Skipping the whole layer (omit, never stub)."
    return
}
$haveFuse      = Test-Path $Fuse
$haveCodegraph = [bool](Get-Command codegraph -ErrorAction SilentlyContinue)
$haveSerena    = [bool](Get-Command serena -ErrorAction SilentlyContinue)

$allArms = @('native')
if ($haveFuse)      { $allArms += 'fuse' }      else { Write-Warning "fuse.exe not built; fuse arm omitted." }
if ($haveCodegraph) { $allArms += 'codegraph' } else { Write-Warning "codegraph not installed; codegraph arm omitted." }
if ($haveSerena)    { $allArms += 'serena' }    else { Write-Warning "serena not installed; serena arm omitted." }

# `pwsh -File ... -Arms native,fuse` passes the comma list as one string; split it back out.
if ($Arms) { $Arms = @($Arms | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
if ($Arms) { $armsToRun = @($Arms | Where-Object { $allArms -contains $_ }) }
else       { $armsToRun = $allArms }
Write-Host "Arms this run: $($armsToRun -join ', ')  (model $Model, rollouts $Rollouts, maxturns $MaxTurns)" -ForegroundColor Cyan

# --- Sample selection (deterministic, logged) ---
$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json
if ($Repos) {
    $Repos = @($Repos | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $prs = $prs | Where-Object { $Repos -contains $_.repo }
}
$sampled = @()
foreach ($grp in ($prs | Group-Object repo)) {
    if ($Full) { $sampled += $grp.Group; continue }
    # Deterministic but meaningful: the first $PerRepo PRs whose title is not merge-noise and whose
    # C# change set is non-empty, so the tool comparison is informative (a CI/whitespace PR with no
    # C# target scores 0 for every arm and tells us nothing). Order is the file order, so it is fixed.
    $eligible = @($grp.Group | Where-Object { @($_.changed_cs).Count -ge 1 -and $_.title -notmatch '(?i)^merge ' })
    $take = $eligible | Select-Object -First $PerRepo
    if (-not $take) { $take = $grp.Group | Select-Object -First $PerRepo }  # fall back rather than drop a repo
    $sampled += $take
}
Write-Host "Sampled PRs ($($sampled.Count)): $((@($sampled | ForEach-Object { "$($_.repo)#$($_.pr)" })) -join ', ')" -ForegroundColor Cyan

# --- Shared: build the per-arm MCP config + allow-list ---
# Server-level allows (mcp__<server>) whitelist every tool a server exposes, so we do not have to
# enumerate each tool name; a minimal Read lets the agent inspect what a scoping tool returned.
$cfgDir = Join-Path $ResultsDir ".out5"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null

function Get-ArmConfig([string]$arm, [string]$wtFull) {
    $mcpArgs = @()
    $allowed = @()
    switch ($arm) {
        'native'    { $allowed = @('Read','Grep','Glob') }
        'fuse' {
            $cfg = Join-Path $cfgDir "fuse.mcp.json"
            $abs = (Resolve-Path $Fuse).Path -replace '\\','/'
            # Per-server timeout (ms) overrides MCP_TOOL_TIMEOUT for this server, so a hung fuse tool call fails fast.
            @{ mcpServers = @{ fuse = @{ command = $abs; args = @('mcp','serve'); timeout = ($McpToolTimeoutSec * 1000) } } } | ConvertTo-Json -Depth 6 | Set-Content $cfg
            $mcpArgs = @('--mcp-config', $cfg, '--strict-mcp-config')
            $allowed = @('mcp__fuse','Read')
        }
        'codegraph' {
            $cfg = Join-Path $cfgDir "codegraph_$([System.IO.Path]::GetFileName($wtFull)).mcp.json"
            # codegraph serve --mcp is a stdio MCP server; point it at the worktree via cwd (the agent runs there).
            @{ mcpServers = @{ codegraph = @{ command = 'codegraph'; args = @('serve','--mcp'); timeout = ($McpToolTimeoutSec * 1000) } } } | ConvertTo-Json -Depth 6 | Set-Content $cfg
            $mcpArgs = @('--mcp-config', $cfg, '--strict-mcp-config')
            $allowed = @('mcp__codegraph','Read')
        }
        'serena' {
            $cfg = Join-Path $cfgDir "serena_$([System.IO.Path]::GetFileName($wtFull)).mcp.json"
            @{ mcpServers = @{ serena = @{ command = 'serena'; args = @('start-mcp-server','--project', $wtFull, '--context','ide-assistant','--enable-web-dashboard','false'); timeout = ($McpToolTimeoutSec * 1000) } } } | ConvertTo-Json -Depth 6 | Set-Content $cfg
            $mcpArgs = @('--mcp-config', $cfg, '--strict-mcp-config')
            $allowed = @('mcp__serena','Read')
        }
    }
    return @{ mcp = $mcpArgs; allowed = $allowed }
}

# --- Shared: parse a stream-json transcript into (toolCalls, cumInTokens, acquiredFiles, model) ---
# Acquired = the files whose CONTENT (or symbol-level slice) the agent actually obtained, defined per arm
# so the measure is fair: a Grep that merely LISTS many file names is not acquisition (the agent did not
# get their content), so the native arm counts only files it actually Read; the scoping arms count what
# their tool delivered (fuse file blocks, codegraph source blocks + symbol refs, serena symbol locations).
function Read-Stream([string]$streamFile, [string]$wtFull, [string]$arm) {
    $toolCalls = 0
    $acquired = New-Object System.Collections.Generic.HashSet[string]
    $resultObj = $null
    $assistantModel = $null
    $wtNorm = ($wtFull -replace '\\','/')
    foreach ($ln in (Get-Content $streamFile)) {
        if (-not $ln.Trim()) { continue }
        try { $o = $ln | ConvertFrom-Json } catch { continue }
        if ($o.type -eq 'result') { $resultObj = $o; continue }
        if ($o.type -eq 'assistant' -and $o.message.content) {
            if (-not $assistantModel -and $o.message.model) { $assistantModel = $o.message.model }
            foreach ($c in $o.message.content) {
                if ($c.type -eq 'tool_use') {
                    $toolCalls++
                    # A direct Read counts as acquisition for every arm (the agent has the content).
                    if ($c.name -eq 'Read' -and $c.input.file_path) {
                        Add-AcquiredPath $acquired $c.input.file_path $wtNorm
                    }
                }
            }
        }
        if ($o.type -eq 'user' -and $o.message.content) {
            foreach ($c in $o.message.content) {
                if ($c.type -eq 'tool_result') {
                    $txt = if ($c.content -is [string]) { $c.content } else { ($c.content | ForEach-Object { $_.text }) -join "`n" }
                    $s = [string]$txt
                    switch ($arm) {
                        'fuse' {
                            # Fuse emits each delivered file as <file path="...">. Precise; no bare-path scan.
                            foreach ($m in [regex]::Matches($s, '<file path="([^"]+)"')) { Add-AcquiredPath $acquired $m.Groups[1].Value $wtNorm }
                        }
                        'codegraph' {
                            # codegraph explore/node returns **`relpath`** source headers and (relpath:line) symbol refs.
                            foreach ($m in [regex]::Matches($s, '\*\*`([^`]+\.cs)`\*\*')) { Add-AcquiredPath $acquired $m.Groups[1].Value $wtNorm }
                            foreach ($m in [regex]::Matches($s, '\(([A-Za-z0-9_./\\-]+\.cs):\d+\)')) { Add-AcquiredPath $acquired $m.Groups[1].Value $wtNorm }
                        }
                        'serena' {
                            # Serena returns symbol locations; capture relative_path JSON fields and path:line tokens.
                            foreach ($m in [regex]::Matches($s, '"(?:relative_path|file|path)"\s*:\s*"([^"]+\.cs)"')) { Add-AcquiredPath $acquired $m.Groups[1].Value $wtNorm }
                            foreach ($m in [regex]::Matches($s, '([A-Za-z0-9_./\\-]+\.cs):\d+')) { Add-AcquiredPath $acquired $m.Groups[1].Value $wtNorm }
                        }
                        default {
                            # native: tool_result is Read content or Grep/Glob listings. A listing is not
                            # acquisition, so we do NOT scan it for paths; only the Read tool_use above counts.
                        }
                    }
                }
            }
        }
    }
    $cumIn = 0
    $modelId = 'unknown'
    if ($resultObj) {
        $cumIn = [int]$resultObj.usage.input_tokens + [int]$resultObj.usage.cache_read_input_tokens + [int]$resultObj.usage.cache_creation_input_tokens
        # Pin the DRIVER model (the assistant messages), not the first modelUsage key: the CLI also bills a
        # small internal helper model (a haiku used for e.g. title summarization), and that helper key can sort
        # ahead of the driver in modelUsage, which would mislabel the pinned model. Prefer the assistant model.
        if ($assistantModel) { $modelId = $assistantModel }
        elseif ($resultObj.modelUsage) {
            $names = @($resultObj.modelUsage.PSObject.Properties.Name)
            # Prefer a non-haiku driver if the driver was a larger model; fall back to the first key.
            $driver = $names | Where-Object { $_ -notmatch '(?i)haiku' } | Select-Object -First 1
            $modelId = if ($driver) { $driver } else { $names | Select-Object -First 1 }
        }
    }
    return @{ toolCalls = $toolCalls; cumIn = $cumIn; acquired = @($acquired); model = $modelId }
}

# Normalize a path (absolute or repo-relative, either slash) to a repo-relative forward-slash path and add it.
function Add-AcquiredPath($set, [string]$raw, [string]$wtNorm) {
    if (-not $raw) { return }
    $p = ($raw -replace '\\','/')
    if ($p -like "$wtNorm/*") { $p = $p.Substring($wtNorm.Length + 1) }
    elseif ($p -like "$wtNorm*") { $p = $p.Substring($wtNorm.Length).TrimStart('/') }
    $p = $p.TrimStart('./').TrimStart('/')
    if ($p) { [void]$set.Add($p) }
}

# --- Sufficiency judge: a second model call scoring whether the gathered set is enough. Model-scored. ---
function Invoke-Judge([string]$task, $truth, $acquired) {
    $truthList = ($truth -join "`n")
    $acqList = (@($acquired) -join "`n")
    $jp = @"
A developer was asked to gather context to implement this change: "$task".
The files actually changed by the real merged PR (ground truth) are:
$truthList

The developer gathered these files:
$acqList

Did the developer gather enough context to implement the change? Answer with a single token: 1 if the gathered set covers the files needed to implement the change (it may contain extras), 0 if it misses files clearly required. Output only 0 or 1.
"@
    $out = & claude -p $jp --model $Model --output-format json 2>$null | ConvertFrom-Json
    $r = "$($out.result)".Trim()
    if ($r -match '1') { return 1 } elseif ($r -match '0') { return 0 } else { return $null }
}

# --- Main loop ---
$rows = @()
$cgSetupMs = @{}
foreach ($grp in ($sampled | Group-Object repo)) {
    $repoEntry = Get-Corpus | Where-Object { $_.name -eq $grp.Name }
    $repoPath = Resolve-RepoPath $repoEntry
    foreach ($prItem in $grp.Group) {
        $tag = "$($grp.Name)_$($prItem.pr)"
        Write-Host "`n=== $($grp.Name)#$($prItem.pr): $($prItem.title) ===" -ForegroundColor Yellow
        $wt = Join-Path $ResultsDir ".wt5/$tag"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree prune 2>$null
        git -C $repoPath worktree add --detach --force $wt $prItem.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { Write-Warning "  worktree failed; skipping PR"; continue }
        $wtFull = (Resolve-Path $wt).Path

        $truth = @($prItem.changed_cs)
        $task = $prItem.title
        if ($task -match '(?i)^merge ') {
            $task = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
        }
        $prompt = "Gather enough context to implement this change: $task. When you have the files you need, stop and list them as a bullet list of repo-relative paths."

        # codegraph setup: build its index once per worktree (outside the measured trajectory).
        if ($armsToRun -contains 'codegraph') {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            Push-Location $wtFull
            try { & codegraph init --force *> $null } catch { Write-Warning "  codegraph init failed: $_" }
            finally { Pop-Location }
            $sw.Stop()
            $cgSetupMs[$tag] = $sw.ElapsedMilliseconds
            Write-Host "  [setup] codegraph init $($sw.ElapsedMilliseconds) ms (outside trajectory)" -ForegroundColor DarkGray
        }

        foreach ($arm in $armsToRun) {
            $conf = Get-ArmConfig $arm $wtFull
            for ($r = 1; $r -le $Rollouts; $r++) {
                $streamFile = Join-Path $ResultsDir ".out5/$tag.$arm.r$r.jsonl"
                $argv = @('-p', $prompt, '--model', $Model, '--output-format','stream-json','--verbose',
                          '--permission-mode','default','--max-turns', "$MaxTurns",
                          '--allowedTools') + $conf.allowed + $conf.mcp
                # Bounded launch (worktree as cwd): a hung MCP call is cut by MCP_TOOL_TIMEOUT, and a wedged
                # claude itself is cut by the wall-clock backstop, which kills the tree so no MCP child orphans.
                $completed = Invoke-ClaudeBounded $argv $streamFile $wtFull $RolloutTimeoutSec

                if (-not $completed) {
                    Write-Warning "    $arm r$r wedged past the wall-clock budget; omitting this rollout (never stubbed)."
                    continue
                }
                if (-not (Test-Path $streamFile) -or -not (Get-Content $streamFile -ErrorAction SilentlyContinue)) {
                    Write-Warning "    $arm r$r produced no transcript; omitting this rollout (never stubbed)."
                    continue
                }
                $p = Read-Stream $streamFile $wtFull $arm
                $acq = @($p.acquired)
                $truthN = @($truth | ForEach-Object { $_ -replace '\\','/' })
                $hit = @($truthN | Where-Object { $acq -contains $_ })
                $recall = if ($truthN.Count) { [math]::Round($hit.Count / $truthN.Count, 3) } else { 0 }
                $precision = if ($acq.Count) { [math]::Round($hit.Count / $acq.Count, 3) } else { 0 }
                $suff = Invoke-Judge $task $truth $acq

                $rows += [pscustomobject]@{
                    repo = $grp.Name; pr = $prItem.pr; arm = $arm; rollout = $r
                    model = $p.model; tool_calls = $p.toolCalls; cum_input_tokens = $p.cumIn
                    truth = $truthN.Count; acquired = $acq.Count; hits = $hit.Count
                    recall = $recall; precision = $precision; sufficiency = $suff
                    cg_setup_ms = $(if ($arm -eq 'codegraph') { $cgSetupMs[$tag] } else { $null })
                }
                Write-Host ("    {0,-9} r{1}: {2,2} calls, {3,7} tok, recall {4:P0} prec {5:P0} suff {6}" -f `
                    $arm, $r, $p.toolCalls, $p.cumIn, $recall, $precision, $suff)
            }
        }

        if ($armsToRun -contains 'codegraph' -and (Test-Path (Join-Path $wtFull '.codegraph'))) {
            Remove-Item -Recurse -Force (Join-Path $wtFull '.codegraph') -ErrorAction SilentlyContinue
        }
        git -C $repoPath worktree remove --force $wt 2>$null
        Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

if (-not $rows) { Write-Warning "No rows produced; nothing to write."; return }

# --- Persist raw rows ---
$rows | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDir 'layer5-agent.json')
$rows | Export-Csv -NoTypeInformation -Path (Join-Path $ResultsDir 'layer5-agent.csv')

# --- Summary (median + IQR per arm) ---
function Median($xs) {
    $s = @($xs | Sort-Object); $n = $s.Count
    if ($n -eq 0) { return $null }
    if ($n % 2) { return $s[[int](($n-1)/2)] }
    return [math]::Round(($s[$n/2 - 1] + $s[$n/2]) / 2, 1)
}
function Quartile($xs, [double]$q) {
    $s = @($xs | Sort-Object); $n = $s.Count
    if ($n -eq 0) { return $null }
    $pos = $q * ($n - 1); $lo = [math]::Floor($pos); $hi = [math]::Ceiling($pos)
    if ($lo -eq $hi) { return $s[[int]$lo] }
    return [math]::Round($s[[int]$lo] + ($pos - $lo) * ($s[[int]$hi] - $s[[int]$lo]), 1)
}

$runModel = ($rows | Select-Object -ExpandProperty model -Unique) -join ', '
# Run date pins WHEN this model-dependent layer was measured (honesty contract: pin model, version, and date).
$runDate = (Get-Date).ToString('yyyy-MM-dd')
$md = @()
$md += '# Layer 5 results (agent-in-the-loop context sufficiency)'
$md += ''
$md += '> MODEL-DEPENDENT LAYER. These numbers are NOT byte-reproducible. They depend on the model, its'
$md += '> sampling, and the day. Read them as a distribution over rollouts, not a fixed measurement.'
$md += ''
$md += "- Model (pinned): $runModel"
$md += "- Run date: $runDate"
$md += "- Rollouts per (PR, arm): $Rollouts"
$md += "- PRs sampled ($($sampled.Count)): $((@($sampled | ForEach-Object { "$($_.repo)#$($_.pr)" })) -join ', ')"
$md += "- Arms: $($armsToRun -join ', ')"
$md += "- Max turns per rollout: $MaxTurns"
$md += '- Tool restriction enforced per arm via --allowedTools; out-of-arm calls are denied by the CLI.'
$md += '- codegraph index build (codegraph init) is setup, measured separately and excluded from the per-run tool-call and token counts.'
$md += '- Recall and precision are objective (vs the PR change set). Sufficiency is a model-scored 0/1 verdict (labeled model-scored).'
$md += ''
$md += '## Per arm (median and inter-quartile range over all rollouts)'
$md += ''
$md += '| Arm | Tool calls (median, IQR) | Cumulative input tokens (median, IQR) | Mean recall | Mean precision | Sufficiency rate |'
$md += '|-----|--------------------------|---------------------------------------|------------:|---------------:|-----------------:|'
foreach ($arm in $armsToRun) {
    $a = @($rows | Where-Object { $_.arm -eq $arm })
    if (-not $a) { continue }
    $tc = $a.tool_calls; $tk = $a.cum_input_tokens
    $tcMed = Median $tc; $tcLo = Quartile $tc 0.25; $tcHi = Quartile $tc 0.75
    $tkMed = Median $tk; $tkLo = Quartile $tk 0.25; $tkHi = Quartile $tk 0.75
    $rec = [math]::Round(($a | Measure-Object recall -Average).Average, 3)
    $prec = [math]::Round(($a | Measure-Object precision -Average).Average, 3)
    $suffRows = @($a | Where-Object { $null -ne $_.sufficiency })
    $suffRate = if ($suffRows.Count) { [math]::Round((($suffRows | Measure-Object sufficiency -Sum).Sum) / $suffRows.Count, 2) } else { 'n/a' }
    $md += ('| {0} | {1} ({2}-{3}) | {4:N0} ({5:N0}-{6:N0}) | {7:P0} | {8:P0} | {9} |' -f `
        $arm, $tcMed, $tcLo, $tcHi, $tkMed, $tkLo, $tkHi, $rec, $prec, $suffRate)
}
$md += ''
$md += '## Per repo, per arm (mean recall / mean cumulative tokens)'
$md += ''
$md += '| Repo | Arm | Mean recall | Mean tokens | Rollouts |'
$md += '|------|-----|------------:|------------:|---------:|'
foreach ($repoName in (@($rows | Select-Object -ExpandProperty repo -Unique | Sort-Object))) {
    foreach ($arm in $armsToRun) {
        $a = @($rows | Where-Object { $_.repo -eq $repoName -and $_.arm -eq $arm })
        if (-not $a) { continue }
        $rec = [math]::Round(($a | Measure-Object recall -Average).Average, 3)
        $tk = [math]::Round(($a | Measure-Object cum_input_tokens -Average).Average, 0)
        $md += ('| {0} | {1} | {2:P0} | {3:N0} | {4} |' -f $repoName, $arm, $rec, $tk, $a.Count)
    }
}
$md += ''
$md += '## How to read this'
$md += ''
$md += 'The contest is cost to acquire sufficient context. Read tool calls and tokens together with recall:'
$md += 'an arm that gathered fewer files cheaply but missed needed files is not a win. This is a small,'
$md += 'model-dependent sample; the distribution and per-repo rows are shown so a single arm-vs-arm point'
$md += 'is never mistaken for a settled result. Arms whose tool was not installed are omitted, not stubbed.'

$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer5-agent.md')

Write-Host "`nLayer 5 complete -> results/layer5-agent.{json,csv,md}  (model-dependent, $($rows.Count) rows)" -ForegroundColor Green
