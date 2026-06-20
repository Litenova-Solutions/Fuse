# Generate prs.json: real merged-PR change sets used as Layer 2A ground truth.
# For each corpus repo it walks merge commits ("Merge pull request #N ..."), and
# records the ones that change between 2 and 25 .cs files. Each parent pair gives
# the PR base (parent 1) and head (parent 2), so the 2A harness can reconstruct
# the exact change set locally with a worktree. Reproducible from the pinned clone.

. "$PSScriptRoot/common.ps1"

$perRepo = 6
$all = @()

foreach ($repo in Get-Corpus) {
    if ($repo.local) { continue }
    $path = Resolve-RepoPath $repo
    if (-not (Test-Path $path)) { Write-Warning "missing $($repo.name)"; continue }

    $lines = git -C $path log --merges --grep='Merge pull request' '--pretty=format:%H|%P|%s' -n 300
    $picked = 0
    foreach ($line in $lines) {
        if ($picked -ge $perRepo) { break }
        $parts = $line -split '\|', 3
        $merge = $parts[0]
        $parents = $parts[1] -split '\s+'
        if ($parents.Count -lt 2) { continue }
        $base = $parents[0]
        $head = $parents[1]
        $subject = $parts[2]
        if ($subject -notmatch '#(\d+)') { continue }
        $pr = [int]$Matches[1]

        $changed = git -C $path diff --name-only $base $head -- '*.cs' |
            Where-Object { $_ -and ($_ -notmatch '(^|/)(bin|obj)/') -and ($_ -notlike '*.g.cs') -and ($_ -notlike '*.Designer.cs') }
        $changed = @($changed)
        if ($changed.Count -lt 2 -or $changed.Count -gt 25) { continue }

        # Title from the PR head commit subject (more descriptive than the merge subject).
        $title = (git -C $path log -1 '--pretty=format:%s' $head).Trim()

        $all += [pscustomobject]@{
            repo        = $repo.name
            pr          = $pr
            merge       = $merge
            base        = $base
            head        = $head
            title       = $title
            changed_cs  = $changed
        }
        $picked++
    }
    Write-Host "$($repo.name): picked $picked PRs"
}

$all | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $BenchRoot 'prs.json')
Write-Host "Wrote prs.json with $($all.Count) PR change sets."
