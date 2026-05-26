# File: Docker Health Check Script

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.docker"

function Get-EnvValue([string]$Name, [string]$DefaultValue) {
    if (-not (Test-Path $envFile)) {
        return $DefaultValue
    }
    $line = Get-Content -Encoding UTF8 -Path $envFile |
        Where-Object { $_ -match "^\s*$([regex]::Escape($Name))=" } |
        Select-Object -First 1
    if (-not $line) {
        return $DefaultValue
    }
    return ($line -replace "^\s*$([regex]::Escape($Name))=", "").Trim()
}

$apiPort = Get-EnvValue "API_PORT" "5000"
$aiPort = Get-EnvValue "AI_PORT" "8000"
$baseApi = "http://127.0.0.1:$apiPort"
$baseAi = "http://127.0.0.1:$aiPort"

function Invoke-JsonGet([string]$Url) {
    return Invoke-RestMethod -Method GET -Uri $Url -TimeoutSec 8
}

Write-Host "1) Checking AI live endpoint..."
$aiLive = Invoke-JsonGet "$baseAi/live"
if ($aiLive.code -ne 0) {
    throw "AI live check failed: $($aiLive.msg)"
}
Write-Host "   AI process is live."

Write-Host "2) Checking AI readiness..."
$ai = Invoke-JsonGet "$baseAi/ready"
if ($ai.code -ne 0 -or $ai.model_loaded -ne $true) {
    throw "AI readiness check failed: $($ai.msg)"
}
Write-Host "   AI ready. model_loaded=$($ai.model_loaded)"

Write-Host "3) Checking API health..."
$api = Invoke-JsonGet "$baseApi/api/health"
if ($api.code -ne 0) {
    throw "API health check failed: $($api.msg)"
}
Write-Host "   API healthy. msg=$($api.msg)"

Write-Host "[RESULT] DOCKER STACK HEALTHY"
