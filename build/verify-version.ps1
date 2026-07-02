# Verifies the product version is consistent across every package that ships a version, and, when a release
# tag is supplied, that the tag matches it. The version lives in the codebase (Directory.Build.props is the
# .NET source of truth); this guard is what keeps the tag honest and the sibling manifests from drifting.
#
#   ./build/verify-version.ps1                 # PR check: assert all files agree with each other
#   ./build/verify-version.ps1 -Tag v3.1.2     # release check: also assert the tag matches
#
# Prints the agreed version on success; throws (non-zero exit) on any mismatch.
param(
    [string]$Tag = ""
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
    [pscustomobject]@{ Source = "ext/vscode/package.json"; Version = (Read-Json "ext/vscode/package.json").version }
    [pscustomobject]@{ Source = "mcp-registry/server.json (server)"; Version = $server.version }
    [pscustomobject]@{ Source = "mcp-registry/server.json (package)"; Version = $server.packages[0].version }
    [pscustomobject]@{ Source = "site/package.json"; Version = (Read-Json "site/package.json").version }
)

# @(...) forces an array so a single agreed value does not collapse to a scalar string (whose [0] would be a char).
$distinct = @($sources | ForEach-Object { $_.Version } | Select-Object -Unique)
if ($distinct.Count -ne 1) {
    $detail = ($sources | ForEach-Object { "  $($_.Source) = $($_.Version)" }) -join "`n"
    throw "Version mismatch across packages:`n$detail"
}

$version = $distinct[0]

if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $tagVersion = $Tag -replace '^v', ''
    if ($tagVersion -ne $version) {
        throw "Release tag '$Tag' does not match the codebase version '$version'. Run build/set-version.ps1 $tagVersion and commit before tagging."
    }
}

Write-Host "Version OK: $version"
