# B6: bootstrap confidence intervals for the scoping recall numbers. Reads the per-PR recall already recorded
# in layer2a.json (no fusions are re-run) and reports a 95 percent bootstrap confidence interval for each
# mode's mean recall at the headline budget. With a small PR corpus the intervals are wide, which is the point: it
# keeps the headline deltas honest about sampling uncertainty rather than reading a few-point move as settled.
# Deterministic: the resampling RNG is seeded, so the reported interval is reproducible.
#   pwsh -File tests/benchmarks/harness/bootstrap-ci.ps1

. "$PSScriptRoot/common.ps1"

$Budget     = 50000
$Resamples  = 2000
$Seed       = 1234

$rows = Get-Content (Join-Path $ResultsDir 'layer2a.json') -Raw | ConvertFrom-Json
Get-Random -SetSeed $Seed | Out-Null

# Mean of a resample (with replacement) of the supplied values.
function Resample-Mean([double[]]$values) {
    $n = $values.Length
    $sum = 0.0
    for ($i = 0; $i -lt $n; $i++) { $sum += $values[(Get-Random -Maximum $n)] }
    return $sum / $n
}

$modes = $rows | Where-Object { $_.budget -eq $Budget } | Select-Object -ExpandProperty mode -Unique | Sort-Object

Write-Host "Bootstrap 95% CI for mean recall at budget $Budget ($Resamples resamples, seed $Seed):"
Write-Host ""
Write-Host ("  {0,-10} {1,6}  {2,16}  {3}" -f 'Mode', 'Mean', '95% CI', 'N')
foreach ($mode in $modes) {
    $vals = @($rows | Where-Object { $_.budget -eq $Budget -and $_.mode -eq $mode } | ForEach-Object { [double]$_.recall })
    if ($vals.Count -eq 0) { continue }
    $mean = ($vals | Measure-Object -Average).Average

    $means = New-Object 'double[]' $Resamples
    for ($r = 0; $r -lt $Resamples; $r++) { $means[$r] = Resample-Mean $vals }
    $sorted = $means | Sort-Object
    $lo = $sorted[[int][math]::Floor(0.025 * $Resamples)]
    $hi = $sorted[[int][math]::Floor(0.975 * $Resamples)]

    Write-Host ("  {0,-10} {1,6:P0}  [{2,4:P0}, {3,4:P0}]  {4}" -f $mode, $mean, $lo, $hi, $vals.Count)
}
Write-Host ""
$prCount = @($rows | Where-Object { $_.budget -eq $Budget } | ForEach-Object { "$($_.repo)#$($_.pr)" } | Select-Object -Unique).Count
Write-Host "Note: n=$prCount PRs, so the intervals are wide. A few-point move between releases is within noise;"
Write-Host "trust a delta only when it holds per-repo and across budgets, which the per-repo gate (B9) checks."
