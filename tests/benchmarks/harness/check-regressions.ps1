# B9: per-repo regression gate. Recomputes per-repo per-mode mean recall at the headline budget from a fresh
# layer2a.json and compares it against the committed baseline (layer2a-baseline.json). Exits non-zero if any
# repo's recall drops below the baseline minus the tolerance, so the hard invariant "no per-repo regression at
# the 50k budget" is enforced mechanically rather than by eye. Run after layer2a:
#   pwsh -File tests/benchmarks/harness/layer2a.ps1
#   pwsh -File tests/benchmarks/harness/check-regressions.ps1
# A deliberate, measured improvement updates layer2a-baseline.json in the same commit; the gate never papers
# over a regression by relaxing silently.

. "$PSScriptRoot/common.ps1"

$resultsPath  = Join-Path $ResultsDir 'layer2a.json'
$baselinePath = Join-Path $ResultsDir 'layer2a-baseline.json'

if (-not (Test-Path $resultsPath))  { Write-Error "Missing $resultsPath. Run layer2a.ps1 first."; exit 2 }
if (-not (Test-Path $baselinePath)) { Write-Error "Missing $baselinePath."; exit 2 }

$rows     = Get-Content $resultsPath -Raw | ConvertFrom-Json
$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
$budget    = [int]$baseline.budget
$tolerance = [double]$baseline.tolerance

# Recompute current per-repo per-mode mean recall at the headline budget.
$current = @{}
foreach ($r in $rows | Where-Object { $_.budget -eq $budget }) {
    $key = "$($r.mode)|$($r.repo)"
    if (-not $current.ContainsKey($key)) { $current[$key] = [System.Collections.Generic.List[double]]::new() }
    $current[$key].Add([double]$r.recall)
}

$regressions = @()
$checked = 0
foreach ($mode in $baseline.recall.PSObject.Properties.Name) {
    foreach ($repoProp in $baseline.recall.$mode.PSObject.Properties) {
        $repo = $repoProp.Name
        $base = [double]$repoProp.Value
        $key  = "$mode|$repo"
        if (-not $current.ContainsKey($key)) { Write-Warning "  no current data for $key"; continue }
        $checked++
        $cur = ($current[$key] | Measure-Object -Average).Average
        $delta = $cur - $base
        $flag = if ($delta -lt (-$tolerance)) { 'REGRESSION'; $regressions += "$key $($base.ToString('P0')) -> $($cur.ToString('P0'))" } else { 'ok' }
        Write-Host ("  {0,-26} base {1,5:P0}  now {2,5:P0}  d {3,6:+0.0%;-0.0%; 0.0%}  {4}" -f $key, $base, $cur, $delta, $flag)
    }
}

Write-Host ""
if ($regressions.Count -gt 0) {
    Write-Host "FAIL: $($regressions.Count) per-repo regression(s) beyond tolerance $($tolerance.ToString('P0')):"
    $regressions | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "PASS: $checked per-repo cells at budget $budget, none below baseline minus $($tolerance.ToString('P0'))."
exit 0
