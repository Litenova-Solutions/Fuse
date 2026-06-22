# Layer 1: intrinsic, deterministic metrics over the pinned corpus.
# Per repo and per reduction level (--level none|standard|aggressive|skeleton|publicapi) plus a Repomix arm:
#   token count, reduction ratio vs raw concatenation, cold wall-clock, peak memory,
#   and fidelity (fraction of public types, methods, routes preserved).
#
# The 'none' arm is the former 'default' (no flags); 'aggressive' is the former '--all'
# (level aggressive plus generated-code collapse, which --all used to bundle); 'skeleton' is
# the former '--skeleton'. 'standard' and 'publicapi' are new arms with no prior baseline.
#
# Prerequisites: dotnet SDK and git for the Fuse and raw arms (these run offline against the
# already-cloned corpus). The Repomix arm additionally needs network and `npx` (Node). When
# Repomix is unavailable (no npx, the call throwing, or a stub output below the sanity floor),
# this script does NOT write a broken row: it carries the prior committed Repomix row from the
# existing layer1.json and prints a one-line notice. Every Fuse-versus-raw number is valid
# without npx; only the Fuse-versus-generic-packer row is carried in that case.
#
# Output: results/layer1.json, results/layer1.csv, results/layer1.md

. "$PSScriptRoot/common.ps1"

# A real packer dump of a C# file set is always at least as large as the raw concatenation of those
# files: it adds a preamble and a per-file envelope. A restricted-environment `npx --yes repomix`
# instead emits a ~380-token stub that packed zero files. So a dump below this fraction of the raw
# token count (or with zero type fidelity on a repo that has types) is treated as unusable, not a
# measurement. A relative floor avoids a false positive on the tiny SampleShop fixture, whose genuine
# dump is only a few hundred tokens.
$RepomixRawFraction = 0.5

# Prior committed Repomix rows, keyed by repo, so an unusable fresh run carries the committed row
# instead of publishing a stub. Loaded before this run overwrites layer1.json at the end.
$priorRepomix = @{}
$priorLayer1 = Join-Path $ResultsDir 'layer1.json'
if (Test-Path $priorLayer1) {
    foreach ($row in (Get-Content $priorLayer1 -Raw | ConvertFrom-Json)) {
        if ($row.tool -eq 'repomix') { $priorRepomix[$row.repo] = $row }
    }
}
$haveNpx = [bool](Get-Command npx -ErrorAction SilentlyContinue)
if (-not $haveNpx) { Write-Warning "npx not found; the Repomix arm will carry prior committed rows." }

$arms = @(
    @{ Name = 'none';       Flags = @() }
    @{ Name = 'standard';   Flags = @('--level','standard') }
    @{ Name = 'aggressive'; Flags = @('--level','aggressive','--collapse-generated') }
    @{ Name = 'skeleton';   Flags = @('--level','skeleton') }
    @{ Name = 'publicapi';  Flags = @('--level','publicapi') }
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
        $skelFlag = if ($arm.Name -in @('skeleton','publicapi')) { '--skeleton' } else { $null }
        $fidArgs = @($repoPath, $combined)
        if ($skelFlag) { $fidArgs += $skelFlag }
        $fid = & $Fidelity @fidArgs | ConvertFrom-Json

        # Body-integrity guards the string-literal-preservation fixes. It is only meaningful for the
        # body-preserving levels (skeleton/publicApi drop bodies, so their literal ratio is near zero by
        # design) and it is measured against a redaction-disabled rerun, because redaction deliberately
        # replaces secret literals and would otherwise mask the guard. The fused output is a multi-file XML
        # document, never a single C# compilation unit, so the parse check is not used here.
        $biRatio = $null
        if ($arm.Name -in @('none','standard','aggressive')) {
            $biDir = Join-Path $ResultsDir ".out/$($repo.name)/$($arm.Name)-bi"
            if (Test-Path $biDir) { Remove-Item -Recurse -Force $biDir }
            New-Item -ItemType Directory -Force -Path $biDir | Out-Null
            $biArgs = @('dotnet','--directory', $repoPath, '--output', $biDir, '--overwrite', '--format', 'xml',
                '--tokenizer', 'o200k_base', '--no-cache', '--no-redact') + $arm.Flags
            $null = Measure-Process $Fuse $biArgs
            $biFiles = @(Get-ChildItem -Path $biDir -File | Sort-Object Name)
            if ($biFiles) {
                $biCombined = Join-Path $biDir '_combined.fuse.txt'
                Get-Content -LiteralPath ($biFiles.FullName) -Raw | Set-Content -LiteralPath $biCombined
                $biRatio = (Get-BodyIntegrity $repoPath $biCombined).intactRatio
            }
        }

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
            literals_intact_ratio   = $biRatio
        }
    }

    # Repomix arm (competitor packer). Memory reflects the node launcher, so it is
    # not recorded; wall-clock and tokens are comparable.
    #
    # Reliability: a fresh run is only published when it is a real dump. An unusable result
    # (npx missing, the call throwing, no output, a sub-floor stub, or zero type fidelity on a
    # repo with known types) carries the prior committed row rather than overwriting it with a
    # broken row. This keeps the Fuse-versus-generic-packer comparison valid offline.
    $rmxOut = Join-Path $ResultsDir ".out/$($repo.name)/repomix.xml"
    New-Item -ItemType Directory -Force -Path (Split-Path $rmxOut) | Out-Null
    if (Test-Path $rmxOut) { Remove-Item -Force $rmxOut }
    # npx.cmd is a batch shim, so time the call directly rather than via the
    # process-peak harness (which cannot read a .cmd child's working set).
    $rmx = $null
    if ($haveNpx) {
        try {
            # --no-gitignore --no-default-patterns: the mirror is a plain copy with no .git, and newer
            # repomix excludes every file by default on a non-git directory ("Total Files: 0"). These flags
            # make it enumerate the already-filtered C#-only mirror, matching the file set the other arms see.
            $el = Measure-Command { & npx --yes repomix $repoPath -o $rmxOut --include '**/*.cs' --no-gitignore --no-default-patterns --style xml *> $null }
            $rmx = [pscustomobject]@{ Ms = [int]$el.TotalMilliseconds; PeakMB = $null }
        } catch { Write-Warning "  repomix call failed: $_" }
    }

    $repomixRow = $null
    if ($rmx -and (Test-Path $rmxOut)) {
        $rtokens = Get-Tokens $rmxOut
        $rfid = & $Fidelity @($repoPath, $rmxOut) | ConvertFrom-Json
        # Detect a stub or broken dump: far below the raw concatenation (a real dump is larger than raw),
        # or zero types kept on a repo that has types.
        $unusable = ($rawTokens -gt 0 -and $rtokens -lt ($rawTokens * $RepomixRawFraction)) -or
                    ($rfid.types.total -gt 0 -and $rfid.types.ratio -eq 0)
        if (-not $unusable) {
            $rratio = if ($rawTokens -gt 0) { [math]::Round(1 - ($rtokens / $rawTokens), 4) } else { 0 }
            Write-Host ("  {0,-9}: {1,8} tok  reduce {2,6:P1}  {3,5}ms  types {4:P0} methods {5:P0}" -f `
                'repomix', $rtokens, $rratio, $rmx.Ms, $rfid.types.ratio, $rfid.methods.ratio)
            $repomixRow = [pscustomobject]@{
                repo = $repo.name; size = $repo.size; arm = 'full'; tool = 'repomix'
                cs_files = $csFiles.Count; raw_tokens = $rawTokens; tokens = $rtokens
                reduction_ratio = $rratio; wall_ms = $rmx.Ms; peak_mb = $null
                types_total = $rfid.types.total; types_kept = $rfid.types.preserved; types_ratio = $rfid.types.ratio
                methods_total = $rfid.methods.total; methods_kept = $rfid.methods.preserved; methods_ratio = $rfid.methods.ratio
                routes_total = $rfid.routes.total; routes_kept = $rfid.routes.preserved; routes_ratio = $rfid.routes.ratio
            }
        }
    }

    if (-not $repomixRow) {
        # Carry the prior committed Repomix row, never a stub.
        if ($priorRepomix.ContainsKey($repo.name)) {
            $carried = $priorRepomix[$repo.name]
            if (-not ($carried.PSObject.Properties.Name -contains 'note')) {
                $carried | Add-Member -NotePropertyName note -NotePropertyValue '' -Force
            }
            $carried.note = 'repomix arm from prior committed run; npx repomix unavailable in this environment'
            Write-Host "  repomix unavailable (no network or npx); carrying the prior committed row"
            $repomixRow = $carried
        } else {
            Write-Warning "  repomix unavailable and no prior committed row to carry for $($repo.name); omitting the arm"
        }
    }
    if ($repomixRow) { $rows += $repomixRow }
}

# Emit JSON + CSV + markdown.
$rows | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDir 'layer1.json')
$rows | Export-Csv -NoTypeInformation -Path (Join-Path $ResultsDir 'layer1.csv')

$md = @()
$md += '# Layer 1 results (intrinsic)'
$md += ''
$md += '| Repo | Size | Tool/Mode | Tokens | Reduction | Wall ms | Peak MB | Types | Methods | Routes | Literals |'
$md += '|------|------|-----------|-------:|----------:|--------:|--------:|------:|--------:|-------:|---------:|'
foreach ($r in $rows) {
    $mb = if ($null -eq $r.peak_mb) { 'n/a' } else { $r.peak_mb }
    $lit = if ($null -eq $r.literals_intact_ratio) { 'n/a' } else { "{0:P0}" -f $r.literals_intact_ratio }
    $md += ('| {0} | {1} | {2} | {3} | {4:P1} | {5} | {6} | {7:P0} | {8:P0} | {9} | {10} |' -f `
        $r.repo, $r.size, "$($r.tool)/$($r.arm)", $r.tokens, $r.reduction_ratio, $r.wall_ms, $mb,
        $r.types_ratio, $r.methods_ratio, ("{0:P0} ({1}/{2})" -f $r.routes_ratio, $r.routes_kept, $r.routes_total),
        $lit)
}
$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer1.md')

Write-Host "Layer 1 complete -> results/layer1.{json,csv,md}"
