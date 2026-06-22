# Layer 3: a simulated multi-turn agent (ILLUSTRATIVE, not a measured agent).
#
# It operationalizes the quadratic-accumulation argument: a stateless agent re-sends
# its whole context every turn, so naive file-by-file exploration pays a prefill cost
# that grows with the square of the number of turns. We compare three strategies and
# record round-trips (K) and cumulative prefill tokens per repo:
#
#   naive    - open files one at a time; turn i re-sends the sum of the first i files.
#   guided   - fuse_toc to survey, then one budgeted fuse_search to fetch (K=2).
#   ask      - a single budgeted fuse_ask-style retrieval (K=1).
#
# These are model outputs grounded in real Fuse token counts, NOT a benchmarked agent.
# Treat K and prefill as illustrative of the round-trip structure, not as measurements.
#
# Output: results/layer3.json

. "$PSScriptRoot/common.ps1"

$Budget = 20000
$Query = 'core service implementation and request handling'
$rows = @()

foreach ($repo in Get-Corpus) {
    $repoPath = Resolve-RepoPath $repo
    if (-not (Test-Path $repoPath)) {
        Write-Warning "Missing repo $($repo.name) at $repoPath; run setup-corpus.ps1. Skipping."
        continue
    }

    $mirror = New-CsMirror $repoPath $repo.name
    $csFiles = Get-CsFiles $mirror

    # Per-file token sizes for the naive quadratic model. Cap at 60 files so the model stays bounded on huge
    # repos; the cap is reported so a truncated naive cost is never mistaken for the full one.
    $cap = 60
    $capped = $csFiles.Count -gt $cap
    $files = $csFiles | Select-Object -First $cap
    $perFile = @()
    foreach ($f in $files) { $perFile += (Get-Tokens $f.FullName) }

    # naive: turn i re-sends the running sum of the first i files (full-context re-send each turn).
    $naivePrefill = 0; $running = 0
    foreach ($t in $perFile) { $running += $t; $naivePrefill += $running }
    $naiveK = $perFile.Count

    # guided: fuse_toc (survey) then one budgeted search.
    $tocDir = Join-Path $ResultsDir ".out/$($repo.name)/l3-toc"
    if (Test-Path $tocDir) { Remove-Item -Recurse -Force $tocDir }
    New-Item -ItemType Directory -Force -Path $tocDir | Out-Null
    & $Fuse dotnet --directory $mirror --output $tocDir --overwrite --toc --tokenizer o200k_base --no-cache *> $null
    $tocFile = @(Get-ChildItem -Path $tocDir -File | Sort-Object Name)[0]
    $tocTokens = if ($tocFile) { Get-Tokens $tocFile.FullName } else { 0 }

    $searchDir = Join-Path $ResultsDir ".out/$($repo.name)/l3-search"
    if (Test-Path $searchDir) { Remove-Item -Recurse -Force $searchDir }
    New-Item -ItemType Directory -Force -Path $searchDir | Out-Null
    & $Fuse dotnet --directory $mirror --output $searchDir --overwrite --query $Query --max-tokens $Budget --level standard --tokenizer o200k_base --no-cache *> $null
    $searchFile = @(Get-ChildItem -Path $searchDir -File | Sort-Object Name)[0]
    $searchTokens = if ($searchFile) { Get-Tokens $searchFile.FullName } else { 0 }

    # guided is two turns: turn1 prefill = toc, turn2 prefill = toc + search result.
    $guidedPrefill = $tocTokens + ($tocTokens + $searchTokens)
    $guidedK = 2

    # ask: a single budgeted retrieval. Modeled by the same budgeted search as one call.
    $askTokens = $searchTokens
    $askPrefill = $askTokens
    $askK = 1

    Write-Host ("  {0,-15}: naive K={1} prefill={2}  guided K={3} prefill={4}  ask K={5} prefill={6}" -f `
        $repo.name, $naiveK, $naivePrefill, $guidedK, $guidedPrefill, $askK, $askPrefill)

    $rows += [pscustomobject]@{
        repo               = $repo.name
        size               = $repo.size
        cs_files           = $csFiles.Count
        naive_capped       = $capped
        naive_k            = $naiveK
        naive_prefill      = $naivePrefill
        guided_k           = $guidedK
        guided_prefill     = $guidedPrefill
        toc_tokens         = $tocTokens
        search_tokens      = $searchTokens
        ask_k              = $askK
        ask_prefill        = $askPrefill
        note               = 'illustrative model, not a measured agent'
    }
}

$rows | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ResultsDir 'layer3.json')
Write-Host "Layer 3 (illustrative round-trip model) complete -> results/layer3.json"
