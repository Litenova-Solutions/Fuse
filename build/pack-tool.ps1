param(
    # Empty means read the single source of truth in Directory.Build.props, so a local pack is correctly
    # versioned without passing anything. CI passes the file version explicitly after verifying it matches the tag.
    [string]$Version = "",
    [string[]]$Rids = @("win-x64", "linux-x64", "osx-x64")
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $root "Directory.Build.props"
    $Version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) { throw "No <Version> found in $propsPath" }
    Write-Host "Using version $Version from Directory.Build.props"
}
$cliProject = Join-Path $root "src/Host/Fuse.Cli/Fuse.Cli.csproj"
$outputDir = Join-Path $root "src/Host/Fuse.Cli/nupkg"
$runtimeRoot = Join-Path $root "artifacts/runtime"

# A .NET global tool package is portable (RID-agnostic), so it cannot be ReadyToRun-compiled at pack time
# (that needs a runtime identifier and fails with NETSDK1094). Fast startup comes from the Fuse.Runtime.<rid>
# self-contained packages below, not from R2R on the portable tool.
Write-Host "Packing framework-dependent Fuse tool..."
dotnet pack $cliProject `
    --configuration Release `
    --output $outputDir `
    -p:Version=$Version

foreach ($rid in $Rids) {
    $profile = "runtime-$rid"
    Write-Host "Publishing self-contained runtime for $rid..."
    dotnet publish $cliProject `
        --configuration Release `
        /p:PublishProfile=$profile `
        /p:Version=$Version

    $publishDir = Join-Path $runtimeRoot $rid
    if (-not (Test-Path $publishDir)) {
        throw "Expected publish output at $publishDir"
    }

    $binaryName = if ($rid.StartsWith("win")) { "fuse.exe" } else { "fuse" }
    $binaryPath = Join-Path $publishDir $binaryName
    if (-not (Test-Path $binaryPath)) {
        $candidate = Get-ChildItem $publishDir -Filter $binaryName -Recurse | Select-Object -First 1
        if ($null -eq $candidate) {
            throw "Native binary not found for $rid in $publishDir"
        }
        $binaryPath = $candidate.FullName
    }

    $runtimePackageId = "Fuse.Runtime.$rid"
    $stageDir = Join-Path $root "artifacts/nupkg-stage/$runtimePackageId"
    $toolsDir = Join-Path $stageDir "tools/$rid/native"
    if (Test-Path $stageDir) {
        Remove-Item $stageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    Copy-Item $binaryPath (Join-Path $toolsDir $binaryName) -Force

    $nuspecPath = Join-Path $stageDir "$runtimePackageId.nuspec"
    @"
<?xml version="1.0" encoding="utf-8"?>
<package>
  <metadata>
    <id>$runtimePackageId</id>
    <version>$Version</version>
    <authors>Litenova Solutions</authors>
    <description>Self-contained runtime asset for Fuse on $rid.</description>
    <license type="expression">MIT</license>
    <projectUrl>https://github.com/Litenova-Solutions/Fuse</projectUrl>
    <tags>fuse;dotnet-tool;self-contained</tags>
  </metadata>
  <files>
    <file src="tools/**" target="" />
  </files>
</package>
"@ | Set-Content -Path $nuspecPath -Encoding UTF8

    $stubProject = Join-Path $stageDir "$runtimePackageId.pack.csproj"
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <IncludeSymbols>false</IncludeSymbols>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NuspecFile>$runtimePackageId.nuspec</NuspecFile>
    <NuspecBasePath>$stageDir</NuspecBasePath>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path $stubProject -Encoding UTF8

    dotnet pack $stubProject --configuration Release --output $outputDir
    if ($LASTEXITCODE -ne 0) { throw "Packing $runtimePackageId failed." }
    Write-Host "Packed $runtimePackageId to $outputDir"
}

Write-Host "Done. Packages in $outputDir"
