# Layer 3: the illustrative prefill-growth model, subordinate to the layer-4 bound.
#
# The measured round-trip claim is layer 4's ground-truth bound: a blind agent must read
# each file a task needs at least once (a structural LOWER BOUND from real PR change sets),
# while Fuse and Repomix each acquire the context in one call. Layer 3 does NOT measure
# round-trips; it only operationalizes the secondary quadratic-accumulation argument: a
# stateless agent re-sends its whole context every turn, so naive file-by-file exploration
# pays a prefill cost that grows with the square of the number of turns. It compares three
# strategies and records round-trips (K) and cumulative prefill tokens per repo:
#
#   naive    - open files one at a time; turn i re-sends the sum of the first i files.
#   guided   - fuse_toc to survey, then one budgeted fuse_search to fetch (K=2).
#   ask      - a single budgeted fuse_ask-style retrieval (K=1).
#
# These are model outputs grounded in real Fuse token counts, NOT a benchmarked agent.
# Treat K and prefill as illustrative of the round-trip STRUCTURE, not as measurements;
# the measured round-trip result is the layer-4 bound folded in below. A live-agent trace
# (driving a programmatic agent across arms) remains out of scope for the keyless harness.
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

# Fold in the measured round-trip bound from layer 4 (ground truth), so layer 3's output leads
# with the bound and keeps the prefill model clearly subordinate and labeled illustrative.
$bound = $null
$layer4Path = Join-Path $ResultsDir 'layer4-scenario.json'
if (Test-Path $layer4Path) {
    $l4 = Get-Content $layer4Path -Raw | ConvertFrom-Json
    $headBudget = ($l4 | Measure-Object budget -Maximum).Maximum
    $noFuse  = $l4 | Where-Object { $_.arm -eq 'no-fuse' -and $_.budget -eq $headBudget }
    $fuseArm = $l4 | Where-Object { $_.arm -eq 'fuse'    -and $_.budget -eq $headBudget }
    if ($noFuse) {
        $bound = [pscustomobject]@{
            source                       = 'layer4-scenario.json (real merged-PR change sets)'
            headline_budget              = $headBudget
            no_fuse_round_trips_mean     = [math]::Round(($noFuse | Measure-Object round_trips -Average).Average, 1)
            no_fuse_round_trips_is_lower_bound = $true
            fuse_round_trips             = 1
            repomix_round_trips          = 1
            fuse_recall_mean             = [math]::Round(($fuseArm | Measure-Object recall -Average).Average, 3)
            note                         = 'A blind agent must read each needed file at least once; Fuse and Repomix acquire the context in one call. This bound is from ground truth, not the illustrative prefill model below.'
        }
        Write-Host ("  layer-4 bound: no-fuse round-trips mean >= {0} (lower bound); fuse/repomix = 1 call" -f $bound.no_fuse_round_trips_mean)
    }
}

$out = [pscustomobject]@{
    round_trip_bound          = $bound
    illustrative_prefill_model = $rows
    note                      = 'round_trip_bound is the measured result; illustrative_prefill_model is a model, not a benchmarked agent.'
}
$out | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ResultsDir 'layer3.json')
Write-Host "Layer 3 (round-trip bound + illustrative prefill model) complete -> results/layer3.json"
