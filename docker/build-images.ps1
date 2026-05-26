# File: Docker Build Images Script

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.docker"
$composeFile = Join-Path $root "docker\docker-compose.yml"
$templateFile = Join-Path $root "docker\.env.docker.example"

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Copy $templateFile to .env.docker, then fill image tags and build base images."
}

function Invoke-DockerCompose([string[]]$ComposeArgs) {
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        & docker-compose @ComposeArgs
    } else {
        & docker compose @ComposeArgs
    }
}

function Get-EnvValue([string]$Name, [string]$DefaultValue) {
    $line = Get-Content -Encoding UTF8 -Path $envFile |
        Where-Object { $_ -match "^\s*$([regex]::Escape($Name))=" } |
        Select-Object -First 1
    if (-not $line) {
        return $DefaultValue
    }
    return ($line -replace "^\s*$([regex]::Escape($Name))=", "").Trim()
}

Write-Host "Env file: $envFile"
Invoke-DockerCompose @("--env-file", "$envFile", "-f", "$composeFile", "build", "ai", "api")
if ($LASTEXITCODE -ne 0) {
    throw "Image build failed."
}

$builtApiImage = Get-EnvValue "API_IMAGE" "aura-api:local"
$builtAiImage = Get-EnvValue "AI_IMAGE" "aura-ai:local"
$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { (Get-Date -Format "yyyyMMdd-HHmmss") }

docker tag $builtApiImage "$apiRepo`:$tag"
docker tag $builtAiImage "$aiRepo`:$tag"

Write-Host ""
Write-Host "Built and tagged:"
Write-Host "  $apiRepo`:$tag"
Write-Host "  $aiRepo`:$tag"
