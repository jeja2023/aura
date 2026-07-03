# File: AI offline retrieval evaluation wrapper

[CmdletBinding()]
param(
    [string]$DatasetPath = "",
    [int]$VectorDim = 512,
    [int]$TopK = 10,
    [double]$MinScore = -1.0,
    [int]$CandidateMultiplier = 8,
    [int]$CandidatePool = 0,
    [int]$AnnProbe = 16,
    [int]$RerankWindow = 30,
    [Nullable[double]]$MinRecall = $null,
    [Nullable[double]]$MinMrr = $null,
    [Nullable[double]]$MaxEmptyRate = $null,
    [switch]$SummaryOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($DatasetPath)) {
    $DatasetPath = Join-Path $repoRoot "ai\retrieval_eval_sample.json"
}

$scriptPath = Join-Path $repoRoot "ai\evaluate_search.py"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "AI evaluation script not found: $scriptPath"
}
if (-not (Test-Path -LiteralPath $DatasetPath)) {
    throw "AI evaluation dataset not found: $DatasetPath"
}

$python = if (-not [string]::IsNullOrWhiteSpace($env:PYTHON)) { $env:PYTHON } else { "python" }
$argsList = @(
    $scriptPath,
    $DatasetPath,
    "--vector-dim", $VectorDim,
    "--top-k", $TopK,
    "--min-score", $MinScore,
    "--candidate-multiplier", $CandidateMultiplier,
    "--candidate-pool", $CandidatePool,
    "--ann-probe", $AnnProbe,
    "--rerank-window", $RerankWindow
)

if ($SummaryOnly) {
    $argsList += "--summary-only"
}
if ($null -ne $MinRecall) {
    $argsList += @("--min-recall", $MinRecall)
}
if ($null -ne $MinMrr) {
    $argsList += @("--min-mrr", $MinMrr)
}
if ($null -ne $MaxEmptyRate) {
    $argsList += @("--max-empty-rate", $MaxEmptyRate)
}

Push-Location (Join-Path $repoRoot "ai")
try {
    & $python @argsList
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
