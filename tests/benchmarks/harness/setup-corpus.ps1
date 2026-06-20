# Clone every corpus repo and check out its pinned commit (from corpus.json).
# Idempotent: skips repos already present at the right commit. In-repo entries
# (those with a "local" path) are left untouched.

. "$PSScriptRoot/common.ps1"

foreach ($repo in Get-Corpus) {
    if ($repo.local) {
        Write-Host "[skip] $($repo.name) is an in-repo fixture: $($repo.local)"
        continue
    }

    $dest = Join-Path $CorpusDir $repo.name
    if (-not (Test-Path (Join-Path $dest '.git'))) {
        Write-Host "[clone] $($repo.name) <- $($repo.url)"
        New-Item -ItemType Directory -Force -Path $CorpusDir | Out-Null
        git clone --quiet $repo.url $dest
    }

    $current = (git -C $dest rev-parse HEAD).Trim()
    if ($current -ne $repo.commit) {
        Write-Host "[checkout] $($repo.name) -> $($repo.commit)"
        git -C $dest fetch --quiet origin $repo.commit 2>$null
        git -C $dest checkout --quiet $repo.commit
    }
    else {
        Write-Host "[ok] $($repo.name) at $($repo.commit.Substring(0,10))"
    }
}

Write-Host "Corpus ready."
