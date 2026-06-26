# Spike (B12): does adding the PR body to the query change recall versus the title alone? The harness uses the
# PR title as the query (a terse task proxy). This fetches each PR's description from the GitHub API and runs a
# title-plus-body arm against the title-only arm at the headline budget. Read the result with the leakage
# caveat: some PR bodies name their changed files outright (an answer leak), others just carry richer feature
# vocabulary, so a gain conflates the two. The bodies are fetched into a file, never into the agent transcript.
# Not committed results.

. "$PSScriptRoot/common.ps1"

$Budget = 50000
$Depth  = 2

$ownerMap = @{
    'MediatR'          = 'jbogard/MediatR'
    'FluentValidation' = 'FluentValidation/FluentValidation'
    'AutoMapper'       = 'AutoMapper/AutoMapper'
    'NewtonsoftJson'   = 'JamesNK/Newtonsoft.Json'
}

$prs = Get-Content (Join-Path $BenchRoot 'prs.json') -Raw | ConvertFrom-Json
$headers = @{ 'User-Agent' = 'fuse-benchmark'; 'Accept' = 'application/vnd.github+json' }

function Get-EmittedPaths($outDir) {
    $f = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $m = [regex]::Matches([System.IO.File]::ReadAllText($f.FullName), '<file path="([^"]+)"')
    return @($m | ForEach-Object { $_.Groups[1].Value -replace '\\','/' })
}
function Recall($emitted, $truth) {
    $tr = @($truth | ForEach-Object { $_ -replace '\\','/' })
    $hit = @($tr | Where-Object { $emitted -contains $_ })
    if ($tr.Count) { return [math]::Round($hit.Count / $tr.Count, 3) } else { return 0 }
}
function Run-Query($wt, $q) {
    $outDir = Join-Path $ResultsDir ".spikeb12/$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $a = @('dotnet','--directory',$wt,'--output',$outDir,'--overwrite','--format','xml','--tokenizer','o200k_base',
        '--no-cache','--no-manifest','--max-tokens',"$Budget",'--query',$q,'--query-top','10','--depth',"$Depth")
    $null = Measure-Process $Fuse $a
    $emitted = Get-EmittedPaths $outDir
    Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
    return $emitted
}

$rows = @()
$leaky = 0
foreach ($g in ($prs | Group-Object repo)) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $g.Name }
    $repoPath = Resolve-RepoPath $repo
    $slug = $ownerMap[$g.Name]
    foreach ($pr in $g.Group) {
        $title = $pr.title
        if ($title -match '(?i)^merge ') { continue } # merge-noise titles are excluded from the query benchmark

        $body = ''
        try {
            $resp = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/pulls/$($pr.pr)" -Headers $headers -TimeoutSec 30
            $body = [string]$resp.body
        } catch { Write-Warning "  $($g.Name)#$($pr.pr): body fetch failed; skipping"; continue }

        $truth = @($pr.changed_cs)
        # Does the body name any changed file (an outright answer leak)?
        $namesFile = $false
        foreach ($t in $truth) {
            $leaf = [System.IO.Path]::GetFileName($t)
            if ($body -match [regex]::Escape($leaf)) { $namesFile = $true; break }
        }
        if ($namesFile) { $leaky++ }

        $wt = Join-Path $ResultsDir ".wt/b12_$($g.Name)_$($pr.pr)"
        if (Test-Path $wt) { git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue }
        git -C $repoPath worktree add --detach --force $wt $pr.head 2>$null | Out-Null
        if (-not (Test-Path $wt)) { continue }

        $titleOnly = Recall (Run-Query $wt $title) $truth
        $titleBody = Recall (Run-Query $wt "$title`n$body") $truth
        $rows += [pscustomobject]@{ repo=$g.Name; pr=$pr.pr; namesFile=$namesFile; titleOnly=$titleOnly; titleBody=$titleBody }
        Write-Host ("  {0} #{1,-5} title {2:P0}  title+body {3:P0}  d {4:+0%;-0%; 0%}  {5}" -f `
            $g.Name, $pr.pr, $titleOnly, $titleBody, ($titleBody-$titleOnly), ($(if ($namesFile) {'NAMES-FILE'} else {''})))
        git -C $repoPath worktree remove --force $wt 2>$null; Remove-Item -Recurse -Force $wt -ErrorAction SilentlyContinue
    }
}

$mt = ($rows | Measure-Object titleOnly -Average).Average
$mb = ($rows | Measure-Object titleBody -Average).Average
Write-Host ""
Write-Host ("OVERALL (n={0}): title {1:P0}  title+body {2:P0}  delta {3:+0%;-0%; 0%}; {4} of {0} bodies name a changed file (answer leak)" -f `
    $rows.Count, $mt, $mb, ($mb-$mt), $leaky)
