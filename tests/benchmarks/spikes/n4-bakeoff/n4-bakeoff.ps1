# N4 build-capture bake-off spike.
#
# Per repo, records the tier each mechanism achieves:
#   (a) MSBuildWorkspace (current loader): run the freshly built `fuse index --force` and parse the
#       reported index mode (semantic | partial | syntax).
#   (b) build-capture ladder: tier-1 (oracle-grade) is achievable iff the repo's own `dotnet build`
#       succeeds, because a successful C# build emits Csc invocations in the binary log that
#       CSharpCommandLineParser rehydrates into exact compilations. So mechanism (b)'s tier-1 rate is
#       the plain `dotnet build` success rate. We record build exit and the first error code as the
#       doctor-style reason.
#
# Output: incremental JSON at $OutFile so a partial run is usable.

param(
    [string]$OutFile = "$PSScriptRoot\n4-bakeoff.json",
    [string]$CloneRoot = "$PSScriptRoot\n4-repos",
    [int]$BuildTimeoutSec = 420,
    [int]$IndexTimeoutSec = 420,
    [string]$RepoListJson = "$PSScriptRoot\n4-repos.json"
)

$ErrorActionPreference = "Continue"
$repoRoot = "C:\Projects\Fuse"
$fuseDll = Join-Path $repoRoot "src\Host\Fuse.Cli\bin\Release\net10.0\fuse.dll"
if (-not (Test-Path $fuseDll)) { throw "Built fuse CLI not found at $fuseDll" }
New-Item -ItemType Directory -Force -Path $CloneRoot | Out-Null
$repos = Get-Content $RepoListJson -Raw | ConvertFrom-Json

function Get-BuildTarget([string]$dir) {
    foreach ($ext in @('*.slnx', '*.sln')) {
        $top = Get-ChildItem $dir -Filter $ext -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($top) { return $top.FullName }
    }
    $nested = Get-ChildItem $dir -Recurse -Depth 2 -Include *.slnx, *.sln -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nested) { return $nested.FullName }
    return $dir
}

# Run a process with a timeout. Returns @{ Exit; TimedOut; }. stdout+stderr go to $logFile.
function Run-Proc([string]$exe, [string[]]$argList, [string]$logFile, [int]$timeoutSec, [hashtable]$envVars) {
    if ($envVars) { foreach ($k in $envVars.Keys) { Set-Item -Path "Env:$k" -Value $envVars[$k] } }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill($true) } catch {}
        ($stdout.Result + "`n" + $stderr.Result) | Set-Content $logFile
        return @{ Exit = 124; TimedOut = $true }
    }
    ($stdout.Result + "`n" + $stderr.Result) | Set-Content $logFile
    return @{ Exit = $p.ExitCode; TimedOut = $false }
}

$results = @()
foreach ($r in $repos) {
    $name = $r.name
    Write-Host "===== $name ====="
    $dir = if ($r.local) { Join-Path $repoRoot $r.local } else { Join-Path $CloneRoot $name }

    if (-not $r.local -and -not (Test-Path $dir)) {
        Write-Host "  cloning $($r.url) ..."
        git clone --depth 1 $r.url $dir 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dir)) {
            $results += [ordered]@{ name = $name; clone = "failed"; msbuild_tier = "n/a"; build_exit = -1; build_tier1 = $false; reason = "clone failed" }
            $results | ConvertTo-Json -Depth 5 | Set-Content $OutFile
            continue
        }
    }

    $scanDir = if ($r.subdir) { Join-Path $dir $r.subdir } else { $dir }
    $target = Get-BuildTarget $scanDir
    Write-Host "  target: $target"

    # (b) build-capture: does `dotnet build` succeed?
    Write-Host "  dotnet build ..."
    $binlog = Join-Path $env:TEMP "n4-$name.binlog"
    $buildLog = Join-Path $env:TEMP "n4-$name.build.txt"
    if (Test-Path $binlog) { Remove-Item $binlog -Force -ErrorAction SilentlyContinue }
    $b = Run-Proc "dotnet" @("build", $target, "-c", "Release", "-bl:$binlog") $buildLog $BuildTimeoutSec $null
    $buildExit = $b.Exit
    $reason = ""
    if ($b.TimedOut) {
        $reason = "build timeout ${BuildTimeoutSec}s"
    } elseif ($buildExit -ne 0) {
        $logText = if (Test-Path $buildLog) { Get-Content $buildLog -Raw } else { "" }
        $errMatch = [regex]::Match($logText, '(?m)error\s+([A-Z]{2,}\d{3,})')
        $reason = if ($errMatch.Success) { $errMatch.Groups[1].Value } else { "build failed exit $buildExit" }
    }
    $buildTier1 = ($buildExit -eq 0) -and (Test-Path $binlog)

    # (a) MSBuildWorkspace tier via fuse index (dense off for speed)
    Write-Host "  fuse index ..."
    $indexLog = Join-Path $env:TEMP "n4-$name.index.txt"
    $i = Run-Proc "dotnet" @($fuseDll, "index", $scanDir, "--force") $indexLog $IndexTimeoutSec @{ FUSE_DENSE = "0" }
    $msbuildTier = "syntax"
    if ($i.TimedOut) {
        $msbuildTier = "timeout"
    } else {
        $itext = if (Test-Path $indexLog) { Get-Content $indexLog -Raw } else { "" }
        $m = [regex]::Match($itext, 'Indexed \[(\w+)\]')
        if ($m.Success) { $msbuildTier = $m.Groups[1].Value }
    }

    Write-Host "  => msbuild_tier=$msbuildTier  build_exit=$buildExit  build_tier1=$buildTier1  reason=$reason"
    $results += [ordered]@{
        name = $name; clone = "ok"; msbuild_tier = $msbuildTier
        build_exit = $buildExit; build_tier1 = $buildTier1; reason = $reason
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content $OutFile
}
Write-Host "Done. Results at $OutFile"
