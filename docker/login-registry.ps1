# 文件：Docker 仓库登录脚本（login-registry.ps1） | File: Docker Registry Login Script

$ErrorActionPreference = "Stop"

$registry = if ($env:REGISTRY_HOST) { $env:REGISTRY_HOST } else { throw "请先设置 REGISTRY_HOST。" }
$user = if ($env:REGISTRY_USER) { $env:REGISTRY_USER } else { throw "请先设置 REGISTRY_USER。" }
$pass = if ($env:REGISTRY_PASSWORD) { $env:REGISTRY_PASSWORD } else { throw "请先设置 REGISTRY_PASSWORD。" }

$pass | docker login $registry -u $user --password-stdin
if ($LASTEXITCODE -ne 0) {
    throw "登录失败：$registry"
}

Write-Host "[RESULT] 已登录仓库：$registry"
