# Smoke test for the benchmark harness: runs against the SampleShop fixture only (fast, no corpus clone) and
# asserts the layer-1 row shape, the body-integrity guard, and the compare-gate logic. Exits non-zero on any
# failure so CI can run it without the pinned corpus.
#
#   pwsh -File tests/benchmarks/harness/smoke.ps1

. "$PSScriptRoot/common.ps1"

$failures = @()
function Check($cond, $msg) {
    if (-not $cond) { $script:failures += $msg; Write-Host "FAIL: $msg" -ForegroundColor Red }
    else { Write-Host "ok: $msg" }
}

$sampleShop = Join-Path $RepoRoot 'tests/fixtures/SampleShop'
Check (Test-Path $sampleShop) "SampleShop fixture exists"

# Build a minimal layer-1-shaped row over two levels, with body-integrity, exactly as layer1.ps1 would.
$rows = @()
foreach ($arm in @(
    @{ Name = 'none';       Flags = @() },
    @{ Name = 'aggressive'; Flags = @('--level','aggressive') }
)) {
    $outDir = Join-Path $ResultsDir ".smoke/$($arm.Name)"
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    # --no-redact: the body-integrity guard checks that reduction never alters a literal. Redaction
    # deliberately replaces planted secret literals, which is correct but would mask the guard, so it is
    # disabled for this measurement.
    $fuseArgs = @('dotnet','--directory', $sampleShop, '--output', $outDir, '--overwrite',
        '--format','xml','--tokenizer','o200k_base','--no-cache','--no-redact') + $arm.Flags
    $m = Measure-Process $Fuse $fuseArgs
    Check ($m.ExitCode -eq 0) "fuse run ($($arm.Name)) exits 0"

    $combined = @(Get-ChildItem -Path $outDir -File | Sort-Object Name)[0]
    Check ($null -ne $combined) "fuse produced output ($($arm.Name))"
    if (-not $combined) { continue }

    $tokens = Get-Tokens $combined.FullName
    $fid = & $Fidelity @($sampleShop, $combined.FullName) | ConvertFrom-Json
    $bi  = Get-BodyIntegrity $sampleShop $combined.FullName -ParseCheck:($arm.Name -eq 'none')

    Check ($tokens -gt 0) "token count positive ($($arm.Name))"
    Check ($bi.intactRatio -ge 0.99) "body-integrity intact ratio >= 0.99 ($($arm.Name): $($bi.intactRatio))"

    $rows += [pscustomobject]@{
        repo = 'SampleShop'; arm = $arm.Name; tokens = $tokens
        reduction_ratio = 0.1
        types_ratio = $fid.types.ratio; methods_ratio = $fid.methods.ratio; routes_ratio = $fid.routes.ratio
        literals_intact_ratio = $bi.intactRatio; parses = $bi.parses
    }
}

# Shape assertions on the rows.
Check ($rows.Count -ge 2) "produced >= 2 rows"
foreach ($r in $rows) {
    foreach ($field in @('repo','arm','tokens','types_ratio','methods_ratio','routes_ratio','literals_intact_ratio')) {
        Check ($null -ne $r.$field) "row [$($r.arm)] has field $field"
    }
}

# Compare-gate: identical baseline must pass.
$cmpSame = Compare-Results $rows $rows
Check ($cmpSame.ok) "compare-gate passes against identical baseline"

# Compare-gate: an injected regression (baseline reduction far higher than current) must be flagged.
$inflatedBaseline = $rows | ForEach-Object {
    [pscustomobject]@{
        repo = $_.repo; arm = $_.arm; tokens = $_.tokens
        reduction_ratio = ($_.reduction_ratio + 0.3)
        types_ratio = $_.types_ratio; methods_ratio = $_.methods_ratio; routes_ratio = $_.routes_ratio
        literals_intact_ratio = $_.literals_intact_ratio; parses = $_.parses
    }
}
$cmpRegressed = Compare-Results $rows $inflatedBaseline
Check (-not $cmpRegressed.ok) "compare-gate flags an injected fidelity regression"
Check ($cmpRegressed.regressions.Count -gt 0) "compare-gate reports regression rows"

if ($failures.Count -gt 0) {
    Write-Host "`nSMOKE FAILED: $($failures.Count) check(s)" -ForegroundColor Red
    exit 1
}
Write-Host "`nSMOKE PASSED" -ForegroundColor Green
