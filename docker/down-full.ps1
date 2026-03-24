# 文件：Docker 联调停止脚本（down-full.ps1） | File: Docker Full Down Script

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $root "docker\docker-compose.full.example.yml"

Write-Host "停止并清理容器网络（保留数据卷）..."
docker compose -f "$composeFile" down
if ($LASTEXITCODE -ne 0) {
    throw "Docker compose 停止失败。"
}

Write-Host "已停止。"
