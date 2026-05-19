# 文件：Docker 联调健康检查脚本（check-full.ps1） | File: Docker Full Health Check Script

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$root = Split-Path -Parent $PSScriptRoot
$baseApi = "http://127.0.0.1:5000"
$baseAi = "http://127.0.0.1:8000"

function Invoke-JsonGet([string]$Url) {
    return Invoke-RestMethod -Method GET -Uri $Url -TimeoutSec 8
}

Write-Host "1) 检查 AI 存活..."
$aiLive = Invoke-JsonGet "$baseAi/live"
if ($aiLive.code -ne 0) {
    throw "AI 存活检查失败：$($aiLive.msg)"
}
Write-Host "   AI 进程存活。"

Write-Host "2) 检查 AI 就绪..."
$ai = Invoke-JsonGet "$baseAi/ready"
if ($ai.code -ne 0 -or $ai.model_loaded -ne $true) {
    throw "AI 就绪检查失败：$($ai.msg)"
}
Write-Host "   AI 正常。model_loaded=$($ai.model_loaded)"

Write-Host "3) 检查 API 健康..."
$api = Invoke-JsonGet "$baseApi/api/health"
if ($api.code -ne 0) {
    throw "API 健康检查失败：$($api.msg)"
}
Write-Host "   API 正常。msg=$($api.msg)"

Write-Host "[RESULT] FULL STACK HEALTHY"
