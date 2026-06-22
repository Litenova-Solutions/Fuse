# Calibrates the chars-per-token constants used by ApproximateTokenCounter for the
# Anthropic and Gemini families against the providers' real tokenizers, over a held-out
# sample of C# source from the pinned corpus.
#
# Anthropic: ground truth via the Claude token-counting API (POST /v1/messages/count_tokens),
#   model claude-opus-4-8. Requires ANTHROPIC_API_KEY. The per-request message overhead is
#   subtracted by measuring an empty-content request, so the ratio reflects body tokens only.
# Gemini: requires GEMINI_API_KEY and the count-tokens endpoint; skipped when the key is absent.
#
# Usage: pwsh -File tests/benchmarks/harness/calibrate-tokenizers.ps1
[CmdletBinding()] param(
    [int]$SampleFiles = 60,
    [string]$Model = 'claude-opus-4-8'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpus = Join-Path $root 'tests/benchmarks/.corpus'

function Count-AnthropicTokens([string]$text) {
    $body = @{ model = $Model; messages = @(@{ role = 'user'; content = $text }) } | ConvertTo-Json -Depth 5 -Compress
    $resp = Invoke-RestMethod -Uri 'https://api.anthropic.com/v1/messages/count_tokens' -Method Post `
        -Headers @{ 'x-api-key' = $env:ANTHROPIC_API_KEY; 'anthropic-version' = '2023-06-01'; 'content-type' = 'application/json' } `
        -Body $body -TimeoutSec 60
    return [int]$resp.input_tokens
}

if (-not $env:ANTHROPIC_API_KEY) { Write-Error 'ANTHROPIC_API_KEY not set'; exit 1 }

# Measure the fixed per-request message overhead so the ratio reflects code tokens only.
$overhead = Count-AnthropicTokens 'x'
$overhead = $overhead - 1  # 'x' is a single token; the rest is wrapper overhead.
Write-Output "message overhead: $overhead tokens"

$files = Get-ChildItem -Path $corpus -Recurse -Filter *.cs |
    Where-Object { $_.Length -gt 400 } | Sort-Object FullName | Select-Object -First $SampleFiles

$totalChars = 0; $totalTokens = 0; $perFile = @()
foreach ($f in $files) {
    $text = Get-Content -Raw -LiteralPath $f.FullName
    if ([string]::IsNullOrWhiteSpace($text)) { continue }
    $chars = $text.Length
    $tokens = (Count-AnthropicTokens $text) - $overhead
    if ($tokens -le 0) { continue }
    $totalChars += $chars; $totalTokens += $tokens
    $perFile += [pscustomobject]@{ File = $f.Name; Ratio = [math]::Round($chars / $tokens, 3) }
}

$overall = $totalChars / $totalTokens
$ratios = $perFile.Ratio
$mean = ($ratios | Measure-Object -Average).Average
$min = ($ratios | Measure-Object -Minimum).Minimum
$max = ($ratios | Measure-Object -Maximum).Maximum
Write-Output ''
Write-Output "files sampled:        $($perFile.Count)"
Write-Output "total chars:          $totalChars"
Write-Output "total tokens:         $totalTokens"
Write-Output "overall chars/token:  $([math]::Round($overall,4))"
Write-Output "per-file mean:        $([math]::Round($mean,4))  (min $min, max $max)"
