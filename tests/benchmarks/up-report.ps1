# C1 up-report harness: runs `fuse up --json --probe` over a set of workspaces (synthetic remediation fixtures
# plus the locally-available pinned corpus) and consolidates the machine-readable reports into up-report.json.
# This is the C1 gate artifact per D17: engineered coverage from the synthetic fixtures (a remedy class that
# reproduces deterministically), plus real-world results from the corpus, recorded honestly. Repos in the
# bake-off OSS set that are not provisioned locally are listed as not-run (no silent tail).
#
#   pwsh tests/benchmarks/up-report.ps1 [-Fuse <fuse.dll>] [-Out <path>]
#
# The tier-1 probe runs a real dotnet build per repo, so this is build-heavy; each probe is bounded by the
# engine's 10-minute timeout.
param(
    [string]$Fuse = "src/Host/Fuse.Cli/bin/Release/net10.0/fuse.dll",
    [string]$Out = "tests/benchmarks/results/up-report.json",
    [string]$Generated = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # tests/benchmarks -> tests -> repo root
$fusePath = Join-Path $repoRoot $Fuse
if (-not (Test-Path $fusePath)) { throw "fuse.dll not found at $fusePath; build Fuse.Cli first." }

# The workspaces to probe. group=fixture is engineered coverage (deterministic); group=corpus is the local
# pinned bake-off subset (real-world). apply=$true exercises the install-free overlay remedy and re-probes.
# apply=$true exercises the overlay remedy and re-probes; it never edits the repository (the overlay is a temp
# config passed by RestoreConfigFile). On a repo with no matching blocker the apply is a no-op. allowInstall=$true
# also permits the consent-gated SDK-band install remedy (D17); used for the sdk-pin fixture, which installs the
# pinned .NET band into an isolated directory (never the machine-wide SDK) and re-probes with it.
$targets = @(
    @{ Name = "broken-feed";    Group = "fixture"; Path = "tests/benchmarks/fixtures/remediation/broken-feed"; Apply = $true;  AllowInstall = $false },
    @{ Name = "sdk-pin";        Group = "fixture"; Path = "tests/benchmarks/fixtures/remediation/sdk-pin";      Apply = $true;  AllowInstall = $true },
    @{ Name = "Scrutor";        Group = "corpus";  Path = "tests/benchmarks/.corpus/Scrutor";                  Apply = $true;  AllowInstall = $false },
    @{ Name = "Specification";  Group = "corpus";  Path = "tests/benchmarks/.corpus/Specification";            Apply = $true;  AllowInstall = $false },
    @{ Name = "NodaTime";       Group = "corpus";  Path = "tests/benchmarks/.corpus/NodaTime";                 Apply = $true;  AllowInstall = $false },
    @{ Name = "eShopOnWeb";     Group = "corpus";  Path = "tests/benchmarks/.corpus/eShopOnWeb";               Apply = $true;  AllowInstall = $false }
)

# The bake-off OSS set (n4-bakeoff.json) not provisioned locally, listed so the report never reads as complete
# coverage when it is not. Provisioning these under D:\fuse-work is C1 sub-step 5.
$notProvisioned = @(
    "serilog","Polly","FluentValidation","MediatR","Newtonsoft.Json","RestSharp","AutoFixture","quartznet",
    "AutoMapper","Dapper","Humanizer","StackExchange.Redis","Nancy"
)

$results = @()
foreach ($t in $targets) {
    $path = Join-Path $repoRoot $t.Path
    if (-not (Test-Path $path)) {
        Write-Host "SKIP $($t.Name): not present at $path"
        $results += [ordered]@{ name = $t.Name; group = $t.Group; present = $false }
        continue
    }

    # Clear prior build outputs so the probe reflects a cold build outcome for this run.
    foreach ($d in @("obj","bin")) {
        $p = Join-Path $path $d
        if (Test-Path $p) { Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue }
    }

    $args = @($fusePath, "up", $path, "--json", "--probe")
    if ($t.Apply) { $args += "--apply" }
    if ($t.AllowInstall) { $args += "--allow-install" }
    Write-Host "PROBE $($t.Name) ($($t.Group))..."
    $json = & dotnet @args 2>$null
    try {
        $r = $json | ConvertFrom-Json
    } catch {
        Write-Host "  ERROR: could not parse fuse up --json output for $($t.Name)"
        $results += [ordered]@{ name = $t.Name; group = $t.Group; present = $true; error = "unparseable output" }
        continue
    }

    $bp = $r.buildProbeBefore
    $ap = $r.buildProbeAfter
    $results += [ordered]@{
        name             = $t.Name
        group            = $t.Group
        present          = $true
        loadTier         = $r.before.tier
        loadWorkable     = $r.before.workableSubsetLine
        tier1Attempted   = if ($bp) { $bp.attempted } else { $false }
        tier1Reachable   = if ($bp) { $bp.succeeded } else { $false }
        blockerId        = if ($bp) { $bp.blockerId } else { $null }
        blockerRemedy    = if ($bp) { $bp.blockerRemedy } else { $null }
        applied          = $r.applied
        tier1AfterApply  = if ($ap) { $ap.succeeded } else { $null }
    }
}

$probed = $results | Where-Object { $_.present -eq $true -and $_.tier1Attempted -eq $true }
$reachable = @($probed | Where-Object { $_.tier1Reachable -eq $true }).Count
$blocked = @($probed | Where-Object { $_.tier1Reachable -eq $false })
$blockerCounts = @{}
foreach ($b in $blocked) {
    $k = if ($b.blockerId) { $b.blockerId } else { "unrecognized" }
    $blockerCounts[$k] = ($blockerCounts[$k] + 1)
}

$report = [ordered]@{
    suite       = "up-report"
    description = "C1 fuse up: per-repo tier-1 (build-capture) reachability and the classified blocker, over the synthetic remediation fixtures plus the locally-available pinned corpus. D17: engineered coverage plus real-world flips where possible."
    generated   = $Generated
    environment = [ordered]@{
        note = "Tier-1 reachability is a real dotnet build per repo (the design-time load does not surface restore/build failures). NU1507 is a warning in this SDK band, so the broken-feed fixture escalates it to an error to exercise the remedy deterministically."
    }
    summary     = [ordered]@{
        probed          = $probed.Count
        tier1_reachable = $reachable
        blocked         = $blocked.Count
        blockers        = $blockerCounts
        not_provisioned = $notProvisioned
    }
    repos       = $results
}

$outPath = Join-Path $repoRoot $Out
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $outPath -Encoding utf8
Write-Host "Wrote $outPath"
Write-Host "tier-1 reachable: $reachable / $($probed.Count) probed; blocked: $($blocked.Count)"
