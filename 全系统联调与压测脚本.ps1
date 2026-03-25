# 文件：全系统联调与压测脚本（全系统联调与压测脚本.ps1） | File: Full System Integration and Stress Test Script
# 用途：登录、模拟抓拍、触发研判并校验输出。

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
$base = "https://localhost:5001"
$adminPassword = $env:AURA_ADMIN_PASSWORD
if ([string]::IsNullOrWhiteSpace($adminPassword)) {
    throw "Please set environment variable AURA_ADMIN_PASSWORD (no built-in default password)."
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = "") {
    $headers = @{}
    if ($Token -ne "") { $headers["Authorization"] = "Bearer $Token" }
    if ($null -ne $Body) {
        # Always use ConvertTo-Json to build valid JSON body.
        $json = $Body | ConvertTo-Json -Compress -Depth 12
        return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers -Body $json -ContentType "application/json; charset=utf-8"
    }
    return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers
}

Write-Host "1) login..."
try {
    $login = Invoke-Api "POST" "/api/auth/login" @{ userName = "admin"; password = $adminPassword }
    $token = $login.data.token
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "login failed: token is empty."
    }

    Write-Host "2) bulk mock capture (200)..."
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 1; $i -le 200; $i++) {
        [void](Invoke-Api "POST" "/api/capture/mock" @{
            deviceId = 1
            channelNo = 1
            metadataJson = "{""batch"":""perf"",""idx"":$i}"
        } $token)
    }
    $sw.Stop()
    Write-Host ("   capture elapsed(ms): " + $sw.ElapsedMilliseconds)

    Write-Host "3) run daily judge..."
    [void](Invoke-Api "POST" "/api/judge/run/daily" @{ date = (Get-Date).ToString("yyyy-MM-dd"); cutoffHour = 23 } $token)

    Write-Host "4) query stats and output..."
    $overview = Invoke-Api "GET" "/api/stats/overview" $null $token
    $dash = Invoke-Api "GET" "/api/stats/dashboard" $null $token
    $events = Invoke-Api "GET" "/api/output/events?page=1&pageSize=500" $null $token
    $persons = Invoke-Api "GET" "/api/output/persons?minCapture=1" $null $token

    Write-Host ("   total capture: " + $overview.data.totalCapture)
    Write-Host ("   output events(page): " + (($events.data | Measure-Object).Count))
    Write-Host ("   output persons: " + (($persons.data | Measure-Object).Count))
    Write-Host ("   dashboard daily points: " + (($dash.data.daily | Measure-Object).Count))

    Write-Host "5) export validation..."
    $expCsv = Invoke-Api "GET" "/api/export/csv?dataset=capture" $null $token
    $expXlsx = Invoke-Api "GET" "/api/export/xlsx?dataset=alert" $null $token
    Write-Host ("   csv: " + $expCsv.data.downloadUrl)
    Write-Host ("   xlsx: " + $expXlsx.data.downloadUrl)

    Write-Host "6) alert notify test..."
    $notify = Invoke-Api "POST" "/api/ops/alert-notify-test" @{
        alertType = "integration-self-check"
        detail = "alert notify channel self-check from integration script"
    } $token
    Write-Host ("   notify msg: " + $notify.msg)

    Write-Host "7) alert notify stats..."
    $notifyStats = Invoke-Api "GET" "/api/ops/alert-notify-stats" $null $token
    Write-Host ("   total notify: " + $notifyStats.data.totalNotify)
    Write-Host ("   webhook success/failure: " + $notifyStats.data.webhookSuccess + "/" + $notifyStats.data.webhookFailure)
    Write-Host ("   file success/failure: " + $notifyStats.data.fileSuccess + "/" + $notifyStats.data.fileFailure)
    if ($notifyStats.data.lastFailureAt) {
        Write-Host ("   last failure: [" + $notifyStats.data.lastFailureChannel + "] " + $notifyStats.data.lastFailureReason + " @ " + $notifyStats.data.lastFailureAt)
    }

    Write-Host "8) readiness final gate..."
    $ready = Invoke-Api "GET" "/api/ops/readiness" $null $token
    if ($ready.code -ne 0) {
        throw ("readiness failed: " + $ready.msg)
    }
    $checks = $ready.data.checks
    Write-Host ("   ready: " + $ready.data.ready)
    Write-Host ("   checks: jwt=" + $checks.jwt + " hmac=" + $checks.hmac + " pgsql=" + $checks.pgsql + " redis=" + $checks.redis + " ai=" + $checks.ai + " alertNotify=" + $checks.alertNotify)
    if (-not [bool]$ready.data.ready) {
        $failed = @()
        if (-not [bool]$checks.jwt) { $failed += "jwt" }
        if (-not [bool]$checks.hmac) { $failed += "hmac" }
        if (-not [bool]$checks.pgsql) { $failed += "pgsql" }
        if (-not [bool]$checks.redis) { $failed += "redis" }
        if (-not [bool]$checks.ai) { $failed += "ai" }
        if ($null -ne $checks.alertNotify -and -not [bool]$checks.alertNotify) { $failed += "alertNotify" }
        Write-Host ("[RESULT] NOT READY. exit_code=2. failed checks: " + ($failed -join ", ")) -ForegroundColor Red
        exit 2
    }

    Write-Host "[RESULT] READY. exit_code=0. integration and stress test done." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ("[RESULT] ERROR. exit_code=3. " + $_.Exception.Message) -ForegroundColor Red
    exit 3
}
