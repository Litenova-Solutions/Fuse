# Sets the product version across every package in one step: the .NET source of truth (Directory.Build.props),
# the MCP registry manifest (both version fields), the docs website, and the WinGet manifests under packaging/winget.
# Run this, commit, then tag the matching version; build/verify-version.ps1 enforces the match in CI.
#
#   ./build/set-version.ps1 3.1.2
param(
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# .NET: the single <Version> in Directory.Build.props.
$props = Join-Path $root "Directory.Build.props"
(Get-Content $props -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" |
    Set-Content $props -NoNewline
Write-Host "Directory.Build.props -> $Version"

# MCP registry manifest: both the server version and the package version. Edited by regex so the file layout is
# preserved (ConvertTo-Json would reformat the whole document).
$serverJson = Join-Path $root "mcp-registry/server.json"
(Get-Content $serverJson -Raw) -replace '"version":\s*"[^"]*"', "`"version`": `"$Version`"" |
    Set-Content $serverJson -NoNewline
Write-Host "mcp-registry/server.json -> $Version"

# Docs website: pin package.json to the same version.
$siteJson = Join-Path $root "site/package.json"
(Get-Content $siteJson -Raw) -replace '"version":\s*"[^"]*"', "`"version`": `"$Version`"" |
    Set-Content $siteJson -NoNewline
Write-Host "site/package.json -> $Version"

# WinGet manifests: PackageVersion in all three files; installer URL tracks the release tag.
$wingetDir = Join-Path $root "packaging/winget"
foreach ($manifest in @("Litenova.Fuse.yaml", "Litenova.Fuse.locale.en-US.yaml", "Litenova.Fuse.installer.yaml")) {
    $path = Join-Path $wingetDir $manifest
    $content = Get-Content $path -Raw
    $content = $content -replace 'PackageVersion:\s*[^\r\n]+', "PackageVersion: $Version"
    if ($manifest -eq "Litenova.Fuse.installer.yaml") {
        $content = $content -replace 'InstallerUrl:\s*[^\r\n]+', "InstallerUrl: https://github.com/Litenova-Solutions/Fuse/releases/download/v$Version/fuse-$Version-setup.exe"
    }
    Set-Content $path $content -NoNewline
}
Write-Host "packaging/winget/* -> $Version"

& (Join-Path $PSScriptRoot "verify-version.ps1")
