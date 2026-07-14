# Asserts that RequiresSdk integration tests are covered by both CI legs:
#   - default PR job excludes them (Category!=RequiresSdk)
#   - SDK job runs them on win-x64 and linux-x64 (Category=RequiresSdk)
#
#   ./build/verify-ci-sdk.ps1
#
# Fails when RequiresSdk-tagged tests exist in the tree but either workflow leg is missing
# or misconfigured. Wired into the default CI build job so a PR cannot drop the SDK leg
# while tests still carry the trait.
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Get-RequiresSdkTestFiles {
    $patterns = @(
        'Trait\("Category",\s*"RequiresSdk"\)'
        'Trait\(RequiresSdkIntegration\.TraitName,\s*RequiresSdkIntegration\.TraitValue\)'
    )
    $files = New-Object System.Collections.Generic.HashSet[string]
    $testCs = Get-ChildItem -Path (Join-Path $root "tests") -Recurse -Filter *.cs -File
    foreach ($pattern in $patterns) {
        foreach ($file in $testCs) {
            if (Select-String -Path $file.FullName -Pattern $pattern -Quiet) {
                [void]$files.Add($file.FullName)
            }
        }
    }
    return @($files)
}

function Read-WorkflowText([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path $path)) {
        return $null
    }
    return Get-Content $path -Raw
}

$requiresSdkFiles = Get-RequiresSdkTestFiles
if ($requiresSdkFiles.Count -eq 0) {
    Write-Host "RequiresSdk CI guard OK: no RequiresSdk-tagged tests found."
    exit 0
}

Write-Host "Found $($requiresSdkFiles.Count) file(s) with RequiresSdk trait markers."

$ciText = Read-WorkflowText ".github/workflows/ci.yml"
if ($null -eq $ciText) {
    throw "Missing .github/workflows/ci.yml."
}

$defaultLegPatterns = @(
    'Category!=RequiresSdk'
    'Category\s*!=\s*RequiresSdk'
)
$defaultLegFound = $false
foreach ($pattern in $defaultLegPatterns) {
    if ($ciText -match $pattern) {
        $defaultLegFound = $true
        break
    }
}
if (-not $defaultLegFound) {
    throw @"
RequiresSdk-tagged tests exist but the default CI leg does not exclude them.
Add --filter "Category!=RequiresSdk" to dotnet test in .github/workflows/ci.yml.
Tagged files:
$($requiresSdkFiles -join "`n")
"@
}

$sdkWorkflowPaths = @(
    ".github/workflows/ci-sdk.yml"
    ".github/workflows/ci.yml"
)
$sdkLegText = $null
$sdkWorkflowPath = $null
foreach ($path in $sdkWorkflowPaths) {
    $text = Read-WorkflowText $path
    if ($null -ne $text -and $text -match 'Category=RequiresSdk') {
        $sdkLegText = $text
        $sdkWorkflowPath = $path
        break
    }
}
if ($null -eq $sdkLegText) {
    throw @"
RequiresSdk-tagged tests exist but no CI workflow runs Category=RequiresSdk.
Add .github/workflows/ci-sdk.yml (or an SDK leg in ci.yml) with:
  dotnet test Fuse.slnx -c Release --no-build --filter "Category=RequiresSdk"
Tagged files:
$($requiresSdkFiles -join "`n")
"@
}

if ($sdkLegText -notmatch '10\.0\.x') {
    throw "SDK CI leg in $sdkWorkflowPath must pin the .NET 10 SDK (dotnet-version: '10.0.x')."
}

$requiredRids = @('win-x64', 'linux-x64')
$missingRids = @($requiredRids | Where-Object { $sdkLegText -notmatch $_ })
if ($missingRids.Count -gt 0) {
    throw "SDK CI leg in $sdkWorkflowPath must run on win-x64 and linux-x64. Missing: $($missingRids -join ', ')."
}

Write-Host "RequiresSdk CI guard OK: default leg excludes trait; SDK leg runs on win-x64 and linux-x64 ($sdkWorkflowPath)."
