# File: Docker Up Script

param(
    [switch]$Build
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.docker"
$composeFile = Join-Path $root "docker\docker-compose.yml"
$templateFile = Join-Path $root ".env.docker.example"

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Copy $templateFile to .env.docker, then fill image tags and secrets."
}

function Invoke-DockerCompose([string[]]$ComposeArgs) {
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        & docker-compose @ComposeArgs
    } else {
        & docker compose @ComposeArgs
    }
}

Write-Host "Env file: $envFile"
Write-Host "Compose file: $composeFile"
if ([string]::IsNullOrWhiteSpace($env:COMPOSE_IGNORE_ORPHANS)) {
    $env:COMPOSE_IGNORE_ORPHANS = "true"
}
$composeArgs = @("--env-file", "$envFile", "-f", "$composeFile")
if ($Build) {
    $composeArgs += @("up", "-d", "--build")
} else {
    $composeArgs += @("up", "-d", "--no-build")
}
Invoke-DockerCompose $composeArgs
if ($LASTEXITCODE -ne 0) {
    throw "Docker compose deployment failed to start."
}

Write-Host ""
if ($Build) {
    Write-Host "Started. Images were built/pulled as needed. For offline restarts or updates, use uploaded images and run without -Build."
} else {
    Write-Host "Started. This stack uses existing images by default. Run with -Build only during online bootstrap or local rebuilds."
}
Write-Host "Status command:"
Write-Host "docker compose --env-file `"$envFile`" -f `"$composeFile`" ps"
