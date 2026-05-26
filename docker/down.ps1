# File: Docker Down Script
# Usage: keep named volumes by default. Remove volumes too with: .\docker\down.ps1 -Volumes

param(
    [switch]$Volumes
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.docker"
$composeFile = Join-Path $root "docker\docker-compose.yml"

function Invoke-DockerCompose([string[]]$ComposeArgs) {
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        & docker-compose @ComposeArgs
    } else {
        & docker compose @ComposeArgs
    }
}

$composeArgs = @("--env-file", "$envFile", "-f", "$composeFile")

if ($Volumes) {
    Write-Host "Stopping containers and removing named volumes, including PostgreSQL/Redis/ArangoDB data..."
    Invoke-DockerCompose ($composeArgs + @("down", "-v"))
} else {
    Write-Host "Stopping containers and keeping named volumes..."
    Invoke-DockerCompose ($composeArgs + @("down"))
}
if ($LASTEXITCODE -ne 0) {
    throw "Docker compose deployment failed to stop."
}

Write-Host "Stopped."
