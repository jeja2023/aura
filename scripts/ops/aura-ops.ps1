# File: Aura Ops Entry Script

param(
    [Parameter(Position = 0)]
    [ValidateSet("readiness", "ai-check", "capture-regression", "full-check")]
    [string]$Task = "readiness",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$scriptName = switch ($Task) {
    "readiness" { "readiness-check.ps1" }
    "ai-check" { "ai-check.ps1" }
    "capture-regression" { "capture-regression.ps1" }
    "full-check" { "full-check.ps1" }
}

$scriptPath = Join-Path $scriptRoot $scriptName
if (-not (Test-Path $scriptPath)) {
    throw "Ops script not found: $scriptPath"
}

$scriptText = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
$scriptBlock = [scriptblock]::Create($scriptText)
& $scriptBlock @RemainingArgs
