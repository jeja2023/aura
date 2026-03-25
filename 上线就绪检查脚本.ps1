# 文件：上线就绪检查脚本（上线就绪检查脚本.ps1） | File: Go-live Readiness Check Script
# 用途：检查健康与就绪状态并输出最终结论。
param(
    [string]$User = "",
    [SecureString]$Password
)

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$base = "https://localhost:5001"
$user = if (-not [string]::IsNullOrWhiteSpace($User)) {
    $User
} elseif (-not [string]::IsNullOrWhiteSpace($env:AURA_ADMIN_USER)) {
    $env:AURA_ADMIN_USER
} else {
    "admin"
}
function Convert-SecureStringToPlainText([SecureString]$SecureValue) {
    if ($null -eq $SecureValue) { return "" }
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

$plainPassword = Convert-SecureStringToPlainText $Password
$pass = if (-not [string]::IsNullOrWhiteSpace($plainPassword)) { $plainPassword } else { $env:AURA_ADMIN_PASSWORD }

if ([string]::IsNullOrWhiteSpace($pass)) {
    throw "Please provide -Password or set AURA_ADMIN_PASSWORD. Optional: -User / AURA_ADMIN_USER (default: admin)."
}

function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = "") {
    $headers = @{}
    if ($Token -ne "") { $headers["Authorization"] = "Bearer $Token" }
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Compress -Depth 12
        return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers -Body $json -ContentType "application/json; charset=utf-8"
    }
    return Invoke-RestMethod -Method $Method -Uri "$base$Path" -Headers $headers
}

Write-Host "1) Login..."
try {
    $login = Invoke-Api "POST" "/api/auth/login" @{ userName = $user; password = $pass }
    $token = $login.data.token
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Login failed: empty token."
    }
    Write-Host "   Login OK"

    Write-Host "2) Check /api/health ..."
    $health = Invoke-Api "GET" "/api/health" $null $token
    if ($health.code -ne 0) {
        throw "Health check failed: $($health.msg)"
    }
    Write-Host "   Health OK: $($health.msg)"

    Write-Host "3) Check /api/ops/readiness ..."
    $ready = Invoke-Api "GET" "/api/ops/readiness" $null $token
    if ($ready.code -ne 0) {
        throw "Readiness endpoint failed: $($ready.msg)"
    }

    $envName = $ready.data.environment
    $isReady = [bool]$ready.data.ready
    $checks = $ready.data.checks
    $alertNotify = $ready.data.alertNotify

    Write-Host "   Environment: $envName"
    Write-Host "   Ready: $isReady"
    Write-Host "   Checks: jwt=$($checks.jwt) hmac=$($checks.hmac) pgsql=$($checks.pgsql) redis=$($checks.redis) ai=$($checks.ai) alertNotify=$($checks.alertNotify)"
    if ($null -ne $alertNotify) {
        Write-Host "   AlertNotify window(min): $($alertNotify.healthFailIfRecentFailureMinutes)"
        Write-Host "   AlertNotify hasRecentFailure: $($alertNotify.hasRecentFailure)"
        if ($null -ne $alertNotify.stats) {
            Write-Host "   AlertNotify stats: total=$($alertNotify.stats.totalNotify) webhook=$($alertNotify.stats.webhookSuccess)/$($alertNotify.stats.webhookFailure) file=$($alertNotify.stats.fileSuccess)/$($alertNotify.stats.fileFailure)"
        }
    }

    if (-not $isReady) {
        $failed = @()
        if (-not [bool]$checks.jwt) { $failed += "jwt" }
        if (-not [bool]$checks.hmac) { $failed += "hmac" }
        if (-not [bool]$checks.pgsql) { $failed += "pgsql" }
        if (-not [bool]$checks.redis) { $failed += "redis" }
        if (-not [bool]$checks.ai) { $failed += "ai" }
        if ($null -ne $checks.alertNotify -and -not [bool]$checks.alertNotify) { $failed += "alertNotify" }
        Write-Host ""
        if ($null -ne $alertNotify -and [bool]$alertNotify.hasRecentFailure -and $null -ne $alertNotify.stats) {
            Write-Host "   AlertNotify latest failure: channel=$($alertNotify.stats.lastFailureChannel), reason=$($alertNotify.stats.lastFailureReason), at=$($alertNotify.stats.lastFailureAt)"
        }
        Write-Host "[RESULT] NOT READY. exit_code=2. failed_checks=$($failed -join ', ')" -ForegroundColor Red
        exit 2
    }

    Write-Host ""
    Write-Host "[RESULT] READY. exit_code=0. You can continue go-live process." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[RESULT] ERROR. exit_code=3. $($_.Exception.Message)" -ForegroundColor Red
    exit 3
}
