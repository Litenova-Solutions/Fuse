# Layer 1: intrinsic, deterministic metrics over the pinned corpus.
# Per repo and per reduction mode (default, --all, --skeleton) plus a Repomix arm:
#   token count, reduction ratio vs raw concatenation, cold wall-clock, peak memory,
#   and fidelity (fraction of public types, methods, routes preserved).
#
# Output: results/layer1.json, results/layer1.csv, results/layer1.md

. "$PSScriptRoot/common.ps1"

$arms = @(
    @{ Name = 'default';   Flags = @() }
    @{ Name = 'all';       Flags = @('--all') }
    @{ Name = 'skeleton';  Flags = @('--skeleton') }
)

$rows = @()

foreach ($repo in Get-Corpus) {
    $repoPath = Resolve-RepoPath $repo
    if (-not (Test-Path $repoPath)) {
        Write-Warning "Missing repo $($repo.name) at $repoPath; run setup-corpus.ps1. Skipping."
        continue
    }

    Write-Host "=== $($repo.name) ($($repo.size)) ==="

    # Stage a C#-only mirror so raw concat, Fuse, and Repomix all see one file set.
    $mirror = New-CsMirror $repoPath $repo.name
    $repoPath = $mirror

    # Raw concatenation baseline over the same .cs file set.
    $csFiles = Get-CsFiles $repoPath
    $rawFile = Join-Path $ResultsDir "$($repo.name).raw.cs.txt"
    $sw = [System.IO.StreamWriter]::new($rawFile, $false, [System.Text.Encoding]::UTF8)
    foreach ($f in $csFiles) {
        $rel = $f.FullName.Substring($repoPath.Length).TrimStart('\','/')
        $sw.WriteLine("// FILE: $rel")
        $sw.Write([System.IO.File]::ReadAllText($f.FullName))
        $sw.WriteLine()
    }
    $sw.Close()
    $rawTokens = Get-Tokens $rawFile
    Write-Host ("  raw: {0} files, {1} tokens" -f $csFiles.Count, $rawTokens)

    foreach ($arm in $arms) {
        $outDir = Join-Path $ResultsDir ".out/$($repo.name)/$($arm.Name)"
        if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null

        # The mirror is already C#-only, so the natural DotNet template applies.
        $fuseArgs = @('dotnet','--directory', $repoPath,
            '--output', $outDir, '--overwrite', '--format', 'xml',
            '--tokenizer', 'o200k_base', '--no-cache') + $arm.Flags
        $m = Measure-Process $Fuse $fuseArgs
        $outFiles = @(Get-ChildItem -Path $outDir -File | Sort-Object Name)
        if (-not $outFiles) { Write-Warning "  $($arm.Name): no output"; continue }

        # Large outputs split into parts; concatenate every part so token count and
        # fidelity see the full emission, not just one part.
        $combined = Join-Path $outDir '_combined.fuse.txt'
        Get-Content -LiteralPath ($outFiles.FullName) -Raw | Set-Content -LiteralPath $combined
        $tokens = Get-Tokens $combined
        $skelFlag = if ($arm.Name -eq 'skeleton') { '--skeleton' } else { $null }
        $fidArgs = @($repoPath, $combined)
        if ($skelFlag) { $fidArgs += $skelFlag }
        $fid = & $Fidelity @fidArgs | ConvertFrom-Json

        $ratio = if ($rawTokens -gt 0) { [math]::Round(1 - ($tokens / $rawTokens), 4) } else { 0 }
        Write-Host ("  {0,-9}: {1,8} tok  reduce {2,6:P1}  {3,5}ms  {4,6}MB  types {5:P0} methods {6:P0} routes {7:P0}" -f `
            $arm.Name, $tokens, $ratio, $m.Ms, $m.PeakMB, $fid.types.ratio, $fid.methods.ratio, $fid.routes.ratio)

        $rows += [pscustomobject]@{
            repo            = $repo.name
            size            = $repo.size
            arm             = $arm.Name
            tool            = 'fuse'
            cs_files        = $csFiles.Count
            raw_tokens      = $rawTokens
            tokens          = $tokens
            reduction_ratio = $ratio
            wall_ms         = $m.Ms
            peak_mb         = $m.PeakMB
            types_total     = $fid.types.total
            types_kept      = $fid.types.preserved
            types_ratio     = $fid.types.ratio
            methods_total   = $fid.methods.total
            methods_kept    = $fid.methods.preserved
            methods_ratio   = $fid.methods.ratio
            routes_total    = $fid.routes.total
            routes_kept     = $fid.routes.preserved
            routes_ratio    = $fid.routes.ratio
        }
    }

    # Repomix arm (competitor packer). Memory reflects the node launcher, so it is
    # not recorded; wall-clock and tokens are comparable.
    $rmxOut = Join-Path $ResultsDir ".out/$($repo.name)/repomix.xml"
    New-Item -ItemType Directory -Force -Path (Split-Path $rmxOut) | Out-Null
    if (Test-Path $rmxOut) { Remove-Item -Force $rmxOut }
    # npx.cmd is a batch shim, so time the call directly rather than via the
    # process-peak harness (which cannot read a .cmd child's working set).
    $rmx = $null
    try {
        $el = Measure-Command { & npx --yes repomix $repoPath -o $rmxOut --include '**/*.cs' --style xml *> $null }
        $rmx = [pscustomobject]@{ Ms = [int]$el.TotalMilliseconds; PeakMB = $null }
    } catch { Write-Warning "  repomix failed: $_" }
    if ($rmx -and (Test-Path $rmxOut)) {
        $rtokens = Get-Tokens $rmxOut
        $rfid = & $Fidelity @($repoPath, $rmxOut) | ConvertFrom-Json
        $rratio = if ($rawTokens -gt 0) { [math]::Round(1 - ($rtokens / $rawTokens), 4) } else { 0 }
        Write-Host ("  {0,-9}: {1,8} tok  reduce {2,6:P1}  {3,5}ms  types {4:P0} methods {5:P0}" -f `
            'repomix', $rtokens, $rratio, $rmx.Ms, $rfid.types.ratio, $rfid.methods.ratio)
        $rows += [pscustomobject]@{
            repo = $repo.name; size = $repo.size; arm = 'full'; tool = 'repomix'
            cs_files = $csFiles.Count; raw_tokens = $rawTokens; tokens = $rtokens
            reduction_ratio = $rratio; wall_ms = $rmx.Ms; peak_mb = $null
            types_total = $rfid.types.total; types_kept = $rfid.types.preserved; types_ratio = $rfid.types.ratio
            methods_total = $rfid.methods.total; methods_kept = $rfid.methods.preserved; methods_ratio = $rfid.methods.ratio
            routes_total = $rfid.routes.total; routes_kept = $rfid.routes.preserved; routes_ratio = $rfid.routes.ratio
        }
    }
}

# Emit JSON + CSV + markdown.
$rows | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDir 'layer1.json')
$rows | Export-Csv -NoTypeInformation -Path (Join-Path $ResultsDir 'layer1.csv')

$md = @()
$md += '# Layer 1 results (intrinsic)'
$md += ''
$md += '| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes |'
$md += '|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|'
foreach ($r in $rows) {
    $mb = if ($null -eq $r.peak_mb) { 'n/a' } else { $r.peak_mb }
    $md += ('| {0} | {1} | {2} | {3} | {4:P1} | {5} | {6} | {7:P0} | {8:P0} | {9} |' -f `
        $r.repo, $r.size, "$($r.tool)/$($r.arm)", $r.tokens, $r.reduction_ratio, $r.wall_ms, $mb,
        $r.types_ratio, $r.methods_ratio, ("{0:P0} ({1}/{2})" -f $r.routes_ratio, $r.routes_kept, $r.routes_total))
}
$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer1.md')

Write-Host "Layer 1 complete -> results/layer1.{json,csv,md}"
