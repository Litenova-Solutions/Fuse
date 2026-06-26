# SPIKE (throwaway, not part of the published suite): validate the claude CLI headless mechanism
# that Layer 5 (agent-in-the-loop context sufficiency) depends on, BEFORE building the full layer.
#
# Validates, on one PR, one arm at a time:
#   (a) tool restriction works:  --allowedTools whitelists exactly one arm's toolbox; with -p the
#       agent cannot call anything outside it (denials surface in permission_denials).
#   (b) token accounting works:  --output-format stream-json --verbose emits a final {type:result}
#       line carrying cumulative usage (input/output/cache tokens) and modelUsage (the pinned model id).
#   (c) file-set extraction works: tool_use blocks (native arm) and tool_result payloads (fuse arm)
#       in the stream let us recover which files the agent read or were delivered to it.
#   (d) MCP attach is uniform:    each peer (fuse, codegraph, serena) attaches the SAME way through
#       --mcp-config + --strict-mcp-config; tool restriction scopes a run to just one server's tools.
#
# FINDINGS (recorded here so Layer 5 can be built against a known-good mechanism). Filled by running:
#   pwsh -File tests/benchmarks/harness/spike-layer5.ps1
#
#   - claude --version: 2.1.181 (Claude Code).
#   - Working flags for a scoped headless run:
#       claude -p "<prompt>" --output-format stream-json --verbose
#              --permission-mode default --allowedTools <space-separated tool names>
#              [--mcp-config <file> --strict-mcp-config]   (omit for the native arm)
#     Run with the worktree as cwd (Push-Location) so the agent's project root is the PR head.
#   - JSON shape: stream-json emits one JSON object per line (JSONL). assistant lines carry
#     message.content[] with type=="tool_use" {name,input}; user lines carry type=="tool_result".
#     The final line is {type:"result"} with .usage (cumulative input_tokens, cache_*_input_tokens,
#     output_tokens), .modelUsage (keyed by model id -> the pinned model), .num_turns,
#     .total_cost_usd, .permission_denials (non-empty if an arm tried an out-of-arm tool).
#   - Tool-call count = count of tool_use blocks across assistant lines.
#   - Cumulative input tokens = result.usage.input_tokens + cache_read_input_tokens +
#     cache_creation_input_tokens (the whole-trajectory prefill the agent paid).
#   - Acquired file set:
#       native : tool_use Read -> input.file_path; Grep/Glob -> input.path (dir touched).
#       fuse   : parse <file path="..."> out of each fuse_* tool_result (same regex as layer4
#                Get-EmittedPaths), plus any direct Read file_path.
#   - MCP config that attaches fuse: { mcpServers: { fuse: { command: <abs fuse.exe>, args: ["mcp","serve"] } } }.
#     codegraph and serena attach identically (command+args), so the four arms differ only by
#     --mcp-config presence and the --allowedTools whitelist.
#
# If the CLI could NOT do tool restriction or token accounting, the finding would be "stop, use the
# Agent SDK instead" -- but it can, so Layer 5 is buildable as designed.
#
# MEASURED on PR MediatR#1171 ("Treat blank license key as unconfigured"), truth = 2 files,
# claude 2.1.181, run on this Windows machine (one rollout each, mechanism validation only):
#   native arm: model claude-haiku-4-5-20251001, 9 tool calls, 273,093 cumulative input tokens,
#               1 permission denial (an out-of-arm tool attempt was BLOCKED -> restriction works),
#               acquired 4 files, recall 1.00 / precision 0.50.
#   fuse arm:   model claude-haiku-4-5-20251001, 11 tool calls, 361,141 cumulative input tokens,
#               0 permission denials (stayed inside the fuse_* toolbox -> MCP attach + restriction work),
#               acquired 4 files, recall 1.00 / precision 0.50.
# Mechanism confirmed end to end: stream-json parsing, cumulative token accounting, per-arm tool
# restriction, MCP attach, and file-set extraction all work. (On this trivial 2-file PR with Haiku
# the fuse arm used MORE tokens at equal recall; that is one point, not a trend -- Layer 5 runs N
# rollouts over a 15-20 PR sample to read the distribution, which is the whole point of measuring.)
#
# CAVEATS Layer 5 must honor (from this spike):
#   - PIN THE MODEL. Headless `claude -p` defaulted to claude-haiku-4-5-20251001 here, not the
#     interactive session model. Layer 5 must pass --model <id> and record that id in the results,
#     or the layer silently measures whatever the CLI defaults to (breaks the model-pinned contract).
#   - codegraph and serena are NOT installed on this machine yet. They attach by the identical
#     mechanism proven here for fuse (an mcpServers entry with command+args + --strict-mcp-config);
#     Layer 5 omits any arm whose server is absent (omit, never stub).

param(
    [string]$Repo = 'MediatR',
    [int]$Pr = 0,
    [ValidateSet('native','fuse')]
    [string]$Arm = 'native',
    [switch]$KeepWorktree,
    # Per MCP tool-call timeout (s). The CLI default (~28h) lets a wedged fuse mcp serve hang the spike forever.
    [int]$McpToolTimeoutSec = 120,
    # Wall-clock backstop (s) for the whole claude run; on overrun the process tree is killed (no orphaned server).
    [int]$RolloutTimeoutSec = 600
)

. "$PSScriptRoot/common.ps1"

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Warning "claude CLI not found; Layer 5 spike cannot run. (omit, never stub)"
    return
}

# Bound MCP tool calls and server startup so a hung server cannot wedge the spike. Units are milliseconds.
$env:MCP_TOOL_TIMEOUT = "$($McpToolTimeoutSec * 1000)"
$env:MCP_TIMEOUT = '30000'

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json
if ($Pr -gt 0) {
    $prItem = $prs | Where-Object { $_.repo -eq $Repo -and $_.pr -eq $Pr } | Select-Object -First 1
} else {
    $prItem = $prs | Where-Object { $_.repo -eq $Repo } | Select-Object -First 1
}
if (-not $prItem) { Write-Error "No PR found for $Repo"; return }

$repoEntry = Get-Corpus | Where-Object { $_.name -eq $Repo }
$repoPath = Resolve-RepoPath $repoEntry
$truth = @($prItem.changed_cs)

# Reconstruct the PR head in a worktree (same pattern as layer4).
$wt = Join-Path $ResultsDir ".wt5spike/$($Repo)_$($prItem.pr)"
if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
git -C $repoPath worktree prune 2>$null
git -C $repoPath worktree add --detach --force $wt $prItem.head 2>$null | Out-Null
if (-not (Test-Path $wt)) { Write-Error "worktree failed"; return }
$wtFull = (Resolve-Path $wt).Path
Write-Host "PR#$($prItem.pr) '$($prItem.title)' head=$($prItem.head)"
Write-Host "truth: $($truth -join ', ')"

# merge-noise title fallback (same as layer4).
$task = $prItem.title
if ($task -match '(?i)^merge ') {
    $task = (@($truth | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) }) | Select-Object -Unique) -join ' '
}
$prompt = "Gather enough context to implement this change: $task. When you have the files you need, stop and list them as a bullet list of repo-relative paths."

# Per-arm toolbox.
$mcpConfigArg = @()
$allowed = @()
if ($Arm -eq 'native') {
    $allowed = @('Read','Grep','Glob')
} elseif ($Arm -eq 'fuse') {
    $cfg = Join-Path $ResultsDir ".wt5spike/fuse.mcp.json"
    $fuseAbs = (Resolve-Path $Fuse).Path -replace '\\','/'
    @{ mcpServers = @{ fuse = @{ command = $fuseAbs; args = @('mcp','serve'); timeout = ($McpToolTimeoutSec * 1000) } } } |
        ConvertTo-Json -Depth 5 | Set-Content $cfg
    $mcpConfigArg = @('--mcp-config', $cfg, '--strict-mcp-config')
    # Allow the fuse_* tools plus a minimal Read to inspect what Fuse returned.
    $allowed = @('mcp__fuse__fuse_search','mcp__fuse__fuse_focus','mcp__fuse__fuse_changes',
                 'mcp__fuse__fuse_toc','mcp__fuse__fuse_skeleton','mcp__fuse__fuse_ask','Read')
}

$streamFile = Join-Path $ResultsDir ".wt5spike/$($Repo)_$($prItem.pr).$Arm.jsonl"

$argv = @('-p', $prompt, '--output-format','stream-json','--verbose',
          '--permission-mode','default','--allowedTools') + $allowed + $mcpConfigArg
Write-Host "claude $($argv -join ' ')" -ForegroundColor DarkGray
# Bounded launch (worktree as cwd) via the shared helper: a hung MCP call is cut by MCP_TOOL_TIMEOUT, and a
# wedged claude is cut by the wall-clock backstop, which kills the tree so the fuse mcp serve child cannot orphan.
$completed = Invoke-ClaudeBounded $argv $streamFile $wtFull $RolloutTimeoutSec
if (-not $completed) {
    Write-Warning "claude wedged past ${RolloutTimeoutSec}s; tree killed. Spike inconclusive."
    if (-not $KeepWorktree) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
    return
}

# --- Parse the stream ---
$lines = Get-Content $streamFile
$toolCalls = 0
$acquired = New-Object System.Collections.Generic.HashSet[string]
$resultObj = $null
foreach ($ln in $lines) {
    if (-not $ln.Trim()) { continue }
    try { $o = $ln | ConvertFrom-Json } catch { continue }
    if ($o.type -eq 'result') { $resultObj = $o; continue }
    if ($o.type -eq 'assistant' -and $o.message.content) {
        foreach ($c in $o.message.content) {
            if ($c.type -eq 'tool_use') {
                $toolCalls++
                if ($c.name -eq 'Read' -and $c.input.file_path) {
                    $rel = ($c.input.file_path -replace [regex]::Escape($wtFull),'').TrimStart('\','/') -replace '\\','/'
                    [void]$acquired.Add($rel)
                }
            }
        }
    }
    if ($o.type -eq 'user' -and $o.message.content) {
        foreach ($c in $o.message.content) {
            if ($c.type -eq 'tool_result') {
                $txt = if ($c.content -is [string]) { $c.content } else { ($c.content | ForEach-Object { $_.text }) -join "`n" }
                foreach ($m in [regex]::Matches([string]$txt, '<file path="([^"]+)"')) {
                    [void]$acquired.Add(($m.Groups[1].Value -replace '\\','/'))
                }
            }
        }
    }
}

$cumIn = if ($resultObj) { [int]$resultObj.usage.input_tokens + [int]$resultObj.usage.cache_read_input_tokens + [int]$resultObj.usage.cache_creation_input_tokens } else { 0 }
$modelId = if ($resultObj.modelUsage) { ($resultObj.modelUsage.PSObject.Properties.Name | Select-Object -First 1) } else { 'unknown' }
$denials = if ($resultObj.permission_denials) { @($resultObj.permission_denials).Count } else { 0 }

# recall/precision of the acquired file set vs truth
$acq = @($acquired)
$truthN = @($truth | ForEach-Object { $_ -replace '\\','/' })
$hit = @($truthN | Where-Object { $acq -contains $_ })
$recall = if ($truthN.Count) { [math]::Round($hit.Count / $truthN.Count, 3) } else { 0 }
$precision = if ($acq.Count) { [math]::Round($hit.Count / $acq.Count, 3) } else { 0 }

Write-Host ""
Write-Host "=== SPIKE RESULT ($Arm) ===" -ForegroundColor Cyan
Write-Host "model           : $modelId"
Write-Host "tool calls      : $toolCalls"
Write-Host "cumulative in tk: $cumIn"
Write-Host "permission denials (out-of-arm attempts): $denials"
Write-Host "acquired files  : $($acq.Count)"
$acq | ForEach-Object { Write-Host "    $_" }
Write-Host "recall/precision vs truth: $recall / $precision"
Write-Host "stream saved    : $streamFile"

if (-not $KeepWorktree) {
    git -C $repoPath worktree remove --force $wt 2>$null
    Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
}
