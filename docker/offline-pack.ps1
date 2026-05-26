# File: Docker Offline Package Script

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.docker"
$composeFile = Join-Path $root "docker\docker-compose.yml"
$modelsDir = Join-Path $root "models"
$frontendDir = Join-Path $root "frontend"
$databaseDir = Join-Path $root "database"
$dockerDir = Join-Path $root "docker"

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Copy .env.docker.example to .env.docker first."
}

function Get-EnvValue([string]$Name, [string]$DefaultValue = "") {
    $line = Get-Content -Encoding UTF8 -Path $envFile |
        Where-Object { $_ -match "^\s*$([regex]::Escape($Name))=" } |
        Select-Object -First 1
    if (-not $line) { return $DefaultValue }
    $value = ($line -replace "^\s*$([regex]::Escape($Name))=", "").Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return $DefaultValue }
    return $value
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$packageRoot = Join-Path $root "docker\dist\aura-offline-$timestamp"
$null = New-Item -ItemType Directory -Path $packageRoot -Force
$imagesArchive = Join-Path $packageRoot "aura-images.tar"

$images = @(
    (Get-EnvValue "POSTGRES_IMAGE" "postgres:16-alpine"),
    (Get-EnvValue "REDIS_IMAGE" "redis:7-alpine"),
    (Get-EnvValue "ARANGO_IMAGE" "arangodb:3.12"),
    (Get-EnvValue "API_IMAGE" "aura-api:local"),
    (Get-EnvValue "AI_IMAGE" "aura-ai:local")
)
$images = $images | Select-Object -Unique

Write-Host "==> Exporting images"
docker save -o "$imagesArchive" $images
if ($LASTEXITCODE -ne 0) { throw "Image export failed." }

Write-Host "==> Copying deployment files"
Copy-Item -Path $envFile -Destination (Join-Path $packageRoot ".env.docker") -Force
Copy-Item -Path $composeFile -Destination (Join-Path $packageRoot "docker-compose.yml") -Force
Copy-Item -Path $databaseDir -Destination (Join-Path $packageRoot "database") -Recurse -Force
Copy-Item -Path $frontendDir -Destination (Join-Path $packageRoot "frontend") -Recurse -Force -Exclude @("node_modules", "test-results", "playwright-report")
if (Test-Path $modelsDir) {
    Copy-Item -Path $modelsDir -Destination (Join-Path $packageRoot "models") -Recurse -Force
}

$readme = @"
Aura offline update package: $timestamp

1. Load images:
   docker load -i aura-images.tar

2. Review .env.docker and keep IMAGE_PULL_POLICY=never on the disconnected server.

3. Start or update disconnected deployment:
   docker compose --env-file .env.docker -f docker-compose.yml up -d --no-build

4. Stop while keeping data:
   docker compose --env-file .env.docker -f docker-compose.yml down

5. Stop and remove data volumes only when intentionally wiping the environment:
   docker compose --env-file .env.docker -f docker-compose.yml down -v
"@
Set-Content -Path (Join-Path $packageRoot "README.txt") -Value $readme -Encoding UTF8

Write-Host "[RESULT] Offline package created: $packageRoot"
