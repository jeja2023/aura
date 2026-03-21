# 抓拍链路回归脚本（PowerShell）
# 用途：快速回归登录、抓拍、查询、向量检索、重试队列

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$base = "https://localhost:5001"
$user = "admin"
$pass = "admin123"

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = "") {
    $headers = @{}
    if ($Token -ne "") {
        $headers["Authorization"] = "Bearer $Token"
    }
    if ($Body -ne $null) {
        return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers -Body ($Body | ConvertTo-Json -Depth 8) -ContentType "application/json"
    }
    return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers
}

Write-Host "1) login..."
$login = Invoke-Api "POST" "/api/auth/login" @{ userName = $user; password = $pass }
$token = $login.data.token
Write-Host "   token ok"

Write-Host "2) sdk capture..."
[void](Invoke-Api "POST" "/api/capture/sdk" @{
    deviceId = 1
    channelNo = 1
    timestamp = (Get-Date).ToString("o")
    imageBase64 = "demo-image-base64"
    metadataJson = '{"scene":"regression","mark":"abnormal"}'
} $token)
Write-Host "   sdk ok"

Write-Host "3) onvif capture..."
[void](Invoke-Api "POST" "/api/capture/onvif" @{
    deviceId = 1
    channelNo = 2
    eventTime = (Get-Date).ToString("o")
    imageBase64 = "demo-image-base64-2"
    metadataJson = '{"scene":"regression"}'
} $token)
Write-Host "   onvif ok"

Write-Host "4) capture list..."
$captures = Invoke-Api "GET" "/api/capture/list" $null $token
Write-Host ("   captures: " + (($captures.data | Measure-Object).Count))

Write-Host "5) vector search..."
$vector = Invoke-Api "POST" "/api/vector/search" @{
    feature = @(0.01) * 512
    topK = 5
} $token
Write-Host ("   vector rows: " + (($vector.data | Measure-Object).Count))

Write-Host "6) alert list..."
$alerts = Invoke-Api "GET" "/api/alert/list" $null $token
Write-Host ("   alerts: " + (($alerts.data | Measure-Object).Count))

Write-Host "7) retry status..."
$retryStatus = Invoke-Api "GET" "/api/retry/status" $null $token
Write-Host ("   pending: " + $retryStatus.data.pending)

Write-Host "8) retry process..."
$retryRun = Invoke-Api "POST" "/api/retry/process" @{ take = 20 } $token
Write-Host ("   retry success=" + $retryRun.data.success + ", failed=" + $retryRun.data.failed)

Write-Host "9) operation list..."
$logs = Invoke-Api "GET" "/api/operation/list?page=1&pageSize=20" $null $token
Write-Host ("   logs: " + (($logs.data | Measure-Object).Count))

Write-Host "regression done."
