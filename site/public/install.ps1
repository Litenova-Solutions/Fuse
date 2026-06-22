# Fuse installer for Windows x64.
#
#   irm https://fuse.codes/install.ps1 | iex
#
# Downloads the latest self-contained Fuse binary from GitHub Releases, verifies
# its checksum, installs it to %LOCALAPPDATA%\Programs\Fuse (override with
# FUSE_INSTALL_DIR), and adds that folder to your user PATH. No .NET SDK is
# required. To pin a version, set FUSE_VERSION=v2.0.0. To also register Fuse as
# an MCP server with your agent after install, set FUSE_MCP (currently:
# FUSE_MCP=claude registers it with Claude Code).
#
# .NET developers can instead use: dotnet tool install -g Fuse

$ErrorActionPreference = 'Stop'

$repo = 'Litenova-Solutions/Fuse'
$dir = if ($env:FUSE_INSTALL_DIR) { $env:FUSE_INSTALL_DIR } else { "$env:LOCALAPPDATA\Programs\Fuse" }

$version = if ($env:FUSE_VERSION) { $env:FUSE_VERSION } else {
  (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest").tag_name
}
if (-not $version) { throw "Could not determine the latest version. Set FUSE_VERSION and retry." }

$num = $version.TrimStart('v')
$asset = "fuse-$num-win-x64.zip"
$base = "https://github.com/$repo/releases/download/$version"

$tmp = Join-Path $env:TEMP "fuse-install-$num"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zip = Join-Path $tmp $asset

Write-Host "Downloading $asset ($version) ..."
Invoke-WebRequest "$base/$asset" -OutFile $zip
$sums = (Invoke-WebRequest "$base/SHA256SUMS.txt").Content

Write-Host "Verifying checksum ..."
$expected = ($sums -split "`n" | Where-Object { $_ -match [regex]::Escape($asset) } | Select-Object -First 1).Split(' ')[0].Trim()
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "Checksum mismatch for $asset (expected $expected, got $actual)." }

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Expand-Archive -Path $zip -DestinationPath $dir -Force
Remove-Item $tmp -Recurse -Force

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$dir*") {
  [Environment]::SetEnvironmentVariable('Path', "$userPath;$dir", 'User')
  Write-Host "Added $dir to your user PATH. Open a new terminal to use 'fuse'."
}
Write-Host "Installed fuse $version to $dir\fuse.exe"

# Optional: register Fuse as an MCP server with your agent.
if ($env:FUSE_MCP) {
  switch ($env:FUSE_MCP) {
    'claude' {
      if (Get-Command claude -ErrorAction SilentlyContinue) {
        Write-Host "Registering Fuse with Claude Code ..."
        & claude mcp add fuse --scope user -- "$dir\fuse.exe" serve
      } else {
        Write-Host "FUSE_MCP=claude set, but the 'claude' CLI was not found."
        Write-Host "Install Claude Code, then run: claude mcp add fuse -- fuse serve"
      }
    }
    default { Write-Host "Unknown FUSE_MCP value '$($env:FUSE_MCP)' (supported: claude). Skipping MCP registration." }
  }
}
