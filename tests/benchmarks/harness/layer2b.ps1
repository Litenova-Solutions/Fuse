# Layer 2B: single-turn localization. For each natural-language question with a
# known answer file, does scoping surface that file within a token budget?
#   - query : fuse --query "<question>"
#   - focus : fuse --focus "<seed>"
#   - grep  : rank .cs by question-term hits, fill the budget
# Accuracy is exact-match (answer file present in the surfaced context). We also
# record the token cost to reach the answer. Reproducible at the pinned commits.
#
# Output: results/layer2b.json, results/layer2b.md

. "$PSScriptRoot/common.ps1"

$spec = Get-Content (Join-Path $BenchRoot 'questions.json') -Raw | ConvertFrom-Json
$Budget = [int]$spec.budget
$stop = @('the','and','for','from','into','with','this','that','which','where','file','contains',
          'defines','implemented','handled','holds','provides','such','are','one','its','user',
          'main','through','that','when','then')

$results = @()

function Get-EmittedPaths($outDir) {
    $f = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    return @([regex]::Matches($text, '<file path="([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\','/' })
}

foreach ($q in $spec.questions) {
    $repo = Get-Corpus | Where-Object { $_.name -eq $q.repo }
    $repoPath = (Resolve-Path (Resolve-RepoPath $repo)).Path
    $answer = $q.answer -replace '\\','/'

    $modes = @(
        @{ name='query'; flags=@('--query', $q.question, '--query-top','10','--depth','1') }
        @{ name='focus'; flags=@('--focus', $q.focus_seed, '--depth','1') }
    )

    $line = @{}
    foreach ($mode in $modes) {
        $outDir = Join-Path $ResultsDir ".loc/$($q.repo)_$([math]::Abs($q.question.GetHashCode()))/$($mode.name)"
        if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        $a = @('dotnet','--directory', $repoPath, '--output', $outDir, '--overwrite',
            '--format','xml','--tokenizer','o200k_base','--no-cache','--no-manifest',
            '--max-tokens', "$Budget") + $mode.flags
        $null = Measure-Process $Fuse $a
        $emitted = Get-EmittedPaths $outDir
        $hit = ($emitted -contains $answer)
        $outFile = Get-ChildItem -Path $outDir -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $tok = if ($outFile) { Get-Tokens $outFile.FullName } else { 0 }
        $results += [pscustomobject]@{ repo=$q.repo; question=$q.question; answer=$answer; mode=$mode.name; hit=[int]$hit; tokens=$tok }
        $line[$mode.name] = $hit
    }

    # Grep baseline.
    $terms = @($q.question.ToLower() -split '[^a-z0-9]+' | Where-Object { $_.Length -ge 3 -and $stop -notcontains $_ } | Select-Object -Unique)
    $csFiles = Get-CsFiles $repoPath
    $scored = @()
    foreach ($f in $csFiles) {
        $content = [System.IO.File]::ReadAllText($f.FullName).ToLower()
        $score = 0
        foreach ($t in $terms) { $score += ([regex]::Matches($content, [regex]::Escape($t))).Count }
        if ($score -gt 0) { $scored += [pscustomobject]@{ file=$f; score=$score } }
    }
    $scored = @($scored | Sort-Object score -Descending | Select-Object -First 150)
    $sel=@(); $cum=0; $grepHit=$false
    if ($scored.Count) {
        $toks = & $TokenCount (@($scored | ForEach-Object { $_.file.FullName })) | ConvertFrom-Json
        $tokMap=@{}; foreach ($e in $toks.files) { $tokMap[$e.path]=[int]$e.tokens }
        foreach ($s in $scored) {
            $t = $tokMap[$s.file.FullName]; if ($null -eq $t) { continue }
            if (($cum + $t) -gt $Budget) { continue }
            $cum += $t
            $rel = $s.file.FullName.Substring($repoPath.Length).TrimStart('\','/') -replace '\\','/'
            $sel += $rel
            if ($rel -eq $answer) { $grepHit = $true }
        }
    }
    $results += [pscustomobject]@{ repo=$q.repo; question=$q.question; answer=$answer; mode='grep'; hit=[int]$grepHit; tokens=$cum }

    $mk = { param($b) if ($b) { '+' } else { '-' } }
    Write-Host ("  [{0,-14}] query {1} focus {2} grep {3}  {4}" -f $q.repo, `
        (& $mk $line['query']), (& $mk $line['focus']), (& $mk $grepHit), ($answer.Split('/')[-1]))
}

$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResultsDir 'layer2b.json')
$agg = $results | Group-Object mode | ForEach-Object {
    [pscustomobject]@{
        mode = $_.Name
        accuracy = [math]::Round(($_.Group | Measure-Object hit -Average).Average, 3)
        hits = ($_.Group | Measure-Object hit -Sum).Sum
        n = $_.Count
        mean_tokens = [math]::Round(($_.Group | Measure-Object tokens -Average).Average, 0)
    }
}
$md = @('# Layer 2B results (localization)','',
        "Token budget: $Budget. Questions: $($spec.questions.Count). Accuracy = answer file surfaced (exact match).",'',
        '| Mode | Accuracy | Hits | Mean tokens |',
        '|------|---------:|-----:|------------:|')
foreach ($a in ($agg | Sort-Object mode)) {
    $md += ('| {0} | {1:P0} | {2}/{3} | {4} |' -f $a.mode, $a.accuracy, $a.hits, $a.n, $a.mean_tokens)
}
$md -join "`n" | Set-Content (Join-Path $ResultsDir 'layer2b.md')
$agg | Format-Table | Out-String | Write-Host
Write-Host "Layer 2B complete -> results/layer2b.{json,md}"
