# 文件：Docker 联调停止脚本（down-full.ps1） | File: Docker Full Down Script
# 用法：默认保留命名卷（含 aura-api-storage、数据库卷等）。
#       需要同时删除卷时：.\docker\down-full.ps1 -Volumes

param(
    [switch]$Volumes
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $root "docker\docker-compose.full.example.yml"

if ($Volumes) {
    Write-Host "停止并删除容器及关联卷（含 PostgreSQL/Redis/Arango/aura-api-storage 等，慎用）..."
    docker compose -f "$composeFile" down -v
} else {
    Write-Host "停止并清理容器网络（保留命名卷：数据库与 /app/storage 等数据仍在）..."
    docker compose -f "$composeFile" down
}
if ($LASTEXITCODE -ne 0) {
    throw "Docker compose 停止失败。"
}

Write-Host "已停止。"
