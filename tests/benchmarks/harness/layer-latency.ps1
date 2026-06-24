# Layer Latency (B13): wall-clock of a scoped query call, cold vs warm cache, per corpus repo.
# An MCP agent waits on this call, so its latency is a product metric. Cold is a fresh run with no reduction
# cache and no persistent index; warm reuses both after a warmup run. We report p50 and p95 over repeated
# samples plus peak working set. Per-stage timings (for example reduction time) are not yet surfaced by the CLI
# in a parseable form; this layer measures the end-to-end latency an agent experiences. Stage-level timing is a
# follow-on once the pipeline exposes it.
#
# Output: results/layer-latency.json, results/layer-latency.md

. "$PSScriptRoot/common.ps1"

$Budget   = 50000
$Depth    = 2
$Samples  = 7   # samples per (repo, cache) cell; p95 over 7 is the slowest, p50 the median

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json

function Get-Percentile([double[]]$values, [double]$p) {
    if ($values.Count -eq 0) { return 0 }
    $sorted = @($values | Sort-Object)
    # Nearest-rank percentile: rank = ceil(p/100 * N), 1-based.
    $rank = [math]::Ceiling(($p / 100.0) * $sorted.Count)
    if ($rank -lt 1) { $rank = 1 }
    if ($rank -gt $sorted.Count) { $rank = $sorted.Count }
    return $sorted[$rank - 1]
}

function Invoke-ScopedQuery($repoPath, $query, $outDir, [switch]$Warm) {
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $a = @('dotnet', '--directory', $repoPath, '--output', $outDir, '--overwrite',
           '--query', $query, '--query-top', '10', '--depth', "$Depth",
           '--format', 'xml', '--tokenizer', 'o200k_base',
           '--max-tokens', "$Budget", '--no-manifest')
    # Cold: no reduction cache, no persistent index. Warm: both on, so a repeated call reuses analysis.
    if ($Warm) { $a += @('--index') } else { $a += @('--no-cache') }
    return Measure-Process $Fuse $a
}

$rows = @()
foreach ($repo in (Get-Corpus | Where-Object { $_.name -ne 'SampleShop' })) {
    $repoPath = Resolve-RepoPath $repo
    if (-not (Test-Path $repoPath)) { Write-Warning "skip $($repo.name): not checked out"; continue }

    # A query guaranteed to match: the first PR title recorded for this repo.
    $pr = $prs | Where-Object { $_.repo -eq $repo.name } | Select-Object -First 1
    $query = if ($pr) { $pr.title } else { $repo.name }
    Write-Host "=== $($repo.name): `"$query`" ==="

    $coldOut = Join-Path $ResultsDir ".latency/$($repo.name)/cold"
    $warmOut = Join-Path $ResultsDir ".latency/$($repo.name)/warm"

    $cold = @()
    for ($i = 0; $i -lt $Samples; $i++) {
        $m = Invoke-ScopedQuery $repoPath $query $coldOut
        if ($m.ExitCode -eq 0) { $cold += [double]$m.Ms }
    }

    # Warm up the persistent index and reduction cache once, then measure repeated warm calls.
    $null = Invoke-ScopedQuery $repoPath $query $warmOut -Warm
    $warm = @(); $peak = 0
    for ($i = 0; $i -lt $Samples; $i++) {
        $m = Invoke-ScopedQuery $repoPath $query $warmOut -Warm
        if ($m.ExitCode -eq 0) { $warm += [double]$m.Ms; if ($m.PeakMB -gt $peak) { $peak = $m.PeakMB } }
    }

    $rows += [pscustomobject]@{
        repo       = $repo.name
        cold_p50   = [int](Get-Percentile $cold 50)
        cold_p95   = [int](Get-Percentile $cold 95)
        warm_p50   = [int](Get-Percentile $warm 50)
        warm_p95   = [int](Get-Percentile $warm 95)
        warm_peak_mb = $peak
        samples    = $Samples
    }
    Write-Host ("  cold p50 {0}ms p95 {1}ms  warm p50 {2}ms p95 {3}ms" -f `
        $rows[-1].cold_p50, $rows[-1].cold_p95, $rows[-1].warm_p50, $rows[-1].warm_p95)

    Remove-Item -Recurse -Force $coldOut, $warmOut -ErrorAction SilentlyContinue
}

$rows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultsDir 'layer-latency.json')

$md = @('# Layer Latency results (scoped query call, cold vs warm)','',
        "Scoped fuse query call at a $Budget token budget, depth $Depth, $Samples samples per cell.",
        'Cold: no reduction cache, no persistent index. Warm: persistent index plus reduction cache, after a warmup run.',
        'Times are milliseconds of end-to-end wall clock (the latency an agent waits on).',
        'Absolute times are machine-dependent; read the warm-vs-cold ratio, not the raw numbers, across machines.','',
        '| Repo | Cold p50 | Cold p95 | Warm p50 | Warm p95 | Warm peak MB |',
        '|------|---------:|---------:|---------:|---------:|-------------:|')
foreach ($r in ($rows | Sort-Object repo)) {
    $md += ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $r.repo, $r.cold_p50, $r.cold_p95, $r.warm_p50, $r.warm_p95, $r.warm_peak_mb)
}
$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer-latency.md')
$rows | Format-Table | Out-String | Write-Host
Write-Host "Layer Latency complete -> results/layer-latency.{json,md}"
