# File: Docker GPU network preflight
param(
    [string]$NetworkName = "gpu-bridge",
    [switch]$Create
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker command was not found. Install Docker and start Docker Desktop or the Docker service first."
}

& docker network inspect $NetworkName *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Docker network '$NetworkName' exists."
    exit 0
}

if (-not $Create) {
    Write-Host "Docker network '$NetworkName' does not exist." -ForegroundColor Yellow
    Write-Host "Create it with: powershell -ExecutionPolicy Bypass -File .\docker-gpu-preflight.ps1 -Create"
    exit 2
}

& docker network create $NetworkName
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create Docker network '$NetworkName'."
}

Write-Host "Created Docker network '$NetworkName'."
