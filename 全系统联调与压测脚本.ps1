# 全系统联调与压测脚本（PowerShell）
# 用途：登录、批量模拟抓拍、触发研判、验证输出接口吞吐

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
$base = "https://localhost:5001"

function Call-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = "") {
    $headers = @{}
    if ($Token -ne "") { $headers["Authorization"] = "Bearer $Token" }
    if ($Body -ne $null) {
        return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers -Body ($Body | ConvertTo-Json -Depth 8) -ContentType "application/json"
    }
    return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers
}

Write-Host "1) 登录..."
$login = Call-Api "POST" "/api/auth/login" @{ userName = "admin"; password = "admin123" }
$token = $login.data.token

Write-Host "2) 批量模拟抓拍（200条）..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 1; $i -le 200; $i++) {
    [void](Call-Api "POST" "/api/capture/mock" @{
        deviceId = 1
        channelNo = 1
        metadataJson = "{""batch"":""perf"",""idx"":$i}"
    } $token)
}
$sw.Stop()
Write-Host ("   抓拍耗时(ms): " + $sw.ElapsedMilliseconds)

Write-Host "3) 执行每日研判..."
[void](Call-Api "POST" "/api/judge/run/daily" @{ date = (Get-Date).ToString("yyyy-MM-dd"); cutoffHour = 23 } $token)

Write-Host "4) 拉取统计与输出..."
$overview = Call-Api "GET" "/api/stats/overview" $null $token
$dash = Call-Api "GET" "/api/stats/dashboard" $null $token
$events = Call-Api "GET" "/api/output/events?page=1&pageSize=500" $null $token
$persons = Call-Api "GET" "/api/output/persons?minCapture=1" $null $token

Write-Host ("   总抓拍: " + $overview.data.totalCapture)
Write-Host ("   事件输出数量(页): " + (($events.data | Measure-Object).Count))
Write-Host ("   人员输出数量: " + (($persons.data | Measure-Object).Count))
Write-Host ("   仪表盘维度: daily=" + (($dash.data.daily | Measure-Object).Count))

Write-Host "5) 导出验证..."
$expCsv = Call-Api "GET" "/api/export/csv?dataset=capture" $null $token
$expXlsx = Call-Api "GET" "/api/export/xlsx?dataset=alert" $null $token
Write-Host ("   CSV: " + $expCsv.data.downloadUrl)
Write-Host ("   XLSX: " + $expXlsx.data.downloadUrl)

Write-Host "联调压测完成。"
