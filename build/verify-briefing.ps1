# Fails when briefing.md drifts back to pre-v4 product claims (D15, U1, K1).
# Run from the repo root: ./build/verify-briefing.ps1
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$briefingPath = Join-Path $root "briefing.md"

if (-not (Test-Path $briefingPath)) {
    throw "briefing.md not found at $briefingPath"
}

$content = Get-Content $briefingPath -Raw

$staleMcpTools = @(
    "fuse_index",
    "fuse_map",
    "fuse_localize",
    "fuse_resolve",
    "fuse_neighbors",
    "fuse_signatures",
    "fuse_ask",
    "fuse_changes",
    "fuse_dotnet",
    "fuse_focus",
    "fuse_generic",
    "fuse_search",
    "fuse_skeleton",
    "fuse_toc",
    "fuse_explain"
)

$rules = [System.Collections.Generic.List[object]]::new()
$rules.Add([pscustomobject]@{ Pattern = '(?i)\bfourteen\b'; Message = "MCP tool count must be nine, not fourteen." })
$rules.Add([pscustomobject]@{ Pattern = 'ext/vscode'; Message = "ext/vscode is not a shipped product surface (Decision D15)." })
$rules.Add([pscustomobject]@{ Pattern = 'protocol\.ts'; Message = "TypeScript protocol mirror path must not appear (Decision D15)." })
$rules.Add([pscustomobject]@{ Pattern = 'PROTOCOL_VERSION'; Message = "TypeScript PROTOCOL_VERSION mirror must not appear (Decision D15)." })

foreach ($tool in $staleMcpTools) {
    $escaped = [regex]::Escape($tool)
    $rules.Add([pscustomobject]@{
        Pattern = "(?<![A-Za-z0-9_])$escaped(?![A-Za-z0-9_])"
        Message = "Stale MCP tool name '$tool' must not appear; use the nine-tool loop surface (U1)."
    })
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($rule in $rules) {
    if ($content -match $rule.Pattern) {
        $failures.Add($rule.Message)
    }
}

if ($failures.Count -gt 0) {
    $detail = ($failures | ForEach-Object { "  - $_" }) -join "`n"
    throw "briefing.md drift detected:`n$detail"
}

Write-Host "briefing.md OK"
