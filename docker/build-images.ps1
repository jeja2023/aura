# 文件：Docker 镜像构建脚本（build-images.ps1） | File: Docker Build Images Script

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env"
$composeFile = Join-Path $root "docker\docker-compose.full.example.yml"
$templateFile = Join-Path $root "docker\.env.full.example"

if (-not (Test-Path $envFile)) {
    throw "未找到 $envFile。请先复制 $templateFile 为 .env 并填写变量。"
}

Write-Host "使用环境文件: $envFile"
docker compose --env-file "$envFile" -f "$composeFile" build ai api
if ($LASTEXITCODE -ne 0) {
    throw "镜像构建失败。"
}

$apiRepo = if ($env:API_IMAGE_REPO) { $env:API_IMAGE_REPO } else { "aura-api" }
$aiRepo = if ($env:AI_IMAGE_REPO) { $env:AI_IMAGE_REPO } else { "aura-ai" }
$tag = if ($env:IMAGE_TAG) { $env:IMAGE_TAG } else { (Get-Date -Format "yyyyMMdd-HHmmss") }

docker tag aura-api:local "$apiRepo`:$tag"
docker tag aura-ai:local "$aiRepo`:$tag"

Write-Host ""
Write-Host "构建并打标签完成："
Write-Host "  $apiRepo`:$tag"
Write-Host "  $aiRepo`:$tag"
