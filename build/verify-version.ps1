# Verifies the product version is consistent across every package that ships a version (including WinGet manifests),
# and, when a release tag is supplied, that the tag matches it. The version lives in the codebase (Directory.Build.props is the
# .NET source of truth); this guard is what keeps the tag honest and the sibling manifests from drifting.
#
#   ./build/verify-version.ps1                 # PR check: assert all files agree with each other
#   ./build/verify-version.ps1 -Tag v3.1.2     # release check: also assert the tag matches
#
# Prints the agreed version on success; throws (non-zero exit) on any mismatch.
param(
    [string]$Tag = "",
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Json([string]$relativePath) {
    Get-Content (Join-Path $root $relativePath) -Raw | ConvertFrom-Json
}

$dotnetVersion = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

$server = Read-Json "mcp-registry/server.json"

$sources = @(
    [pscustomobject]@{ Source = "Directory.Build.props"; Version = $dotnetVersion }
    [pscustomobject]@{ Source = "mcp-registry/server.json (server)"; Version = $server.version }
    [pscustomobject]@{ Source = "mcp-registry/server.json (package)"; Version = $server.packages[0].version }
    [pscustomobject]@{ Source = "site/package.json"; Version = (Read-Json "site/package.json").version }
)

foreach ($manifest in @("Litenova.Fuse.yaml", "Litenova.Fuse.locale.en-US.yaml", "Litenova.Fuse.installer.yaml")) {
    $path = Join-Path $root "packaging/winget/$manifest"
    if (Test-Path $path) {
        $match = Select-String -Path $path -Pattern '^PackageVersion:\s*(.+)$' | Select-Object -First 1
        if ($match) {
            $sources += [pscustomobject]@{ Source = "packaging/winget/$manifest"; Version = $match.Matches[0].Groups[1].Value.Trim() }
        }
    }
}

# @(...) forces an array so a single agreed value does not collapse to a scalar string (whose [0] would be a char).
$distinct = @($sources | ForEach-Object { $_.Version } | Select-Object -Unique)
if ($distinct.Count -ne 1) {
    $detail = ($sources | ForEach-Object { "  $($_.Source) = $($_.Version)" }) -join "`n"
    throw "Version mismatch across packages:`n$detail"
}

$version = $distinct[0]

# License consistency (L1: MIT to Apache-2.0). The LICENSE file must be Apache 2.0 and every Fuse-owned
# package manifest that declares a license expression must read Apache-2.0, so a stray MIT claim cannot ship.
$licenseText = Get-Content (Join-Path $root "LICENSE") -Raw
if ($licenseText -notmatch "Apache License") {
    throw "LICENSE is not the Apache License. L1 requires Apache 2.0."
}

$licenseSources = @(
    [pscustomobject]@{ Source = "Directory.Build.props"; Value = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.PackageLicenseExpression | Where-Object { $_ } | Select-Object -First 1 }
    [pscustomobject]@{ Source = "src/Host/Fuse.Cli/Fuse.Cli.csproj"; Value = ([xml](Get-Content (Join-Path $root "src/Host/Fuse.Cli/Fuse.Cli.csproj"))).Project.PropertyGroup.PackageLicenseExpression | Where-Object { $_ } | Select-Object -First 1 }
)
$badLicense = @($licenseSources | Where-Object { $_.Value -and $_.Value -ne "Apache-2.0" })
if ($badLicense.Count -gt 0) {
    $detail = ($badLicense | ForEach-Object { "  $($_.Source) = $($_.Value)" }) -join "`n"
    throw "License expression must be Apache-2.0:`n$detail"
}

if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $tagVersion = $Tag -replace '^v', ''
    if ($tagVersion -ne $version) {
        throw "Release tag '$Tag' does not match the codebase version '$version'. Run build/set-version.ps1 $tagVersion and commit before tagging."
    }
}

# R29: the release safety net. Build the CLI (a normal incremental build, no manual clean) and assert the built
# binary reports the codebase version, so a stale-version bin can never ship. Also assert the slnx Release build
# produces the CLI's Release output (a Debug/Release mixup once left Fuse.Cli without a Release fuse.dll).
if ($Build) {
    $cliProject = Join-Path $root "src/Host/Fuse.Cli/Fuse.Cli.csproj"
    Write-Host "Building $cliProject (Release) to verify the stamped version..."
    & dotnet build $cliProject -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build of Fuse.Cli (Release) failed; cannot verify the built version."
    }

    $fuseDll = Join-Path $root "src/Host/Fuse.Cli/bin/Release/net10.0/fuse.dll"
    if (-not (Test-Path $fuseDll)) {
        throw "Release build did not produce fuse.dll at $fuseDll (a Debug/Release mixup)."
    }

    $reported = (& dotnet $fuseDll --version 2>&1 | Select-Object -First 1).ToString().Trim()
    if ($reported -ne $version) {
        throw "Built fuse --version '$reported' does not match the codebase version '$version'. The incremental build served a stale-version assembly; a version bump must invalidate the compile (see the FuseStampVersionMarker target)."
    }

    Write-Host "Built version OK: fuse --version = $reported (matches $version); Release fuse.dll present."
}

Write-Host "Version OK: $version"
