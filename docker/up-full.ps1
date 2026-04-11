# 文件：Docker 联调启动脚本（up-full.ps1） | File: Docker Full Up Script

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env"
$composeFile = Join-Path $root "docker\docker-compose.full.example.yml"
$templateFile = Join-Path $root "docker\.env.full.example"

if (-not (Test-Path $envFile)) {
    throw "未找到 $envFile。请先复制 $templateFile 为 .env 并填写真实变量。"
}

Write-Host "使用环境文件: $envFile"
Write-Host "启动编排文件: $composeFile"
docker compose --env-file "$envFile" -f "$composeFile" up -d --build
if ($LASTEXITCODE -ne 0) {
    throw "Docker compose 启动失败。"
}

Write-Host ""
Write-Host "启动完成。业务数据持久化：compose 命名卷（含 aura-api-storage → /app/storage）在 down 时默认保留。"
Write-Host "可执行以下命令查看状态："
Write-Host "docker compose -f `"$composeFile`" ps"
