# 文件：AI检索巡检脚本（AI检索巡检脚本.ps1） | File: AI Retrieval Ops Check Script
# 用途：检查 AI 健康状态与检索审计日志，并给出可执行的巡检结论。
param(
    [string]$AiBaseUrl = "http://127.0.0.1:8000",
    [int]$AuditLimit = 100,
    [double]$MaxLatencyMs = 800,
    [int]$MinRemainingQuota = 10,
    [switch]$BackfillFailureAsError,
    [switch]$JsonOutput
)

$ErrorActionPreference = "Stop"

function Invoke-AiApi([string]$Method, [string]$Path) {
    $headers = @{}
    $apiKey = $env:AURA_API_KEY
    if (-not [string]::IsNullOrWhiteSpace($apiKey)) {
        $headers["X-Aura-Ai-Key"] = $apiKey
    }
    return Invoke-RestMethod -Method $Method -Uri "$AiBaseUrl$Path" -Headers $headers
}

function ConvertTo-Boolean($value) {
    if ($null -eq $value) { return $false }
    return [bool]$value
}

function Write-CheckResult([int]$ExitCode, [string]$State, [string]$Message, [array]$Issues, $Metrics, [switch]$JsonOnly) {
    $payload = @{
        exit_code = $ExitCode
        state = $State
        message = $Message
        issues = $Issues
        metrics = $Metrics
        checked_at = (Get-Date).ToString("o")
    }
    if ($JsonOnly) {
        $payload | ConvertTo-Json -Depth 12 -Compress
    } else {
        if ($ExitCode -eq 0) {
            Write-Host ("[RESULT] READY. exit_code=0. " + $Message) -ForegroundColor Green
        } elseif ($ExitCode -eq 2) {
            Write-Host "[RESULT] NOT READY. exit_code=2" -ForegroundColor Red
            foreach ($issue in $Issues) {
                Write-Host (" - " + $issue) -ForegroundColor Red
            }
        } else {
            Write-Host ("[RESULT] ERROR. exit_code=3. " + $Message) -ForegroundColor Red
        }
    }
    exit $ExitCode
}

try {
    if (-not $JsonOutput) {
        Write-Host "1) 检查 AI 健康状态（GET /）..."
    }
    $health = Invoke-AiApi "GET" "/"
    if ($health.code -ne 0) {
        throw ("AI 健康检查失败：" + $health.msg)
    }

    $modelLoaded = ConvertTo-Boolean $health.model_loaded
    $breaker = $health.熔断状态
    $limiter = $health.限流状态
    $backfill = $health.回填状态

    if (-not $modelLoaded) {
        throw "AI 模型未加载成功，请先检查模型初始化日志。"
    }

    $isBreakerOpen = ConvertTo-Boolean $breaker.is_open
    $remaining = [int]$limiter.remaining
    $backfillFailures = [int]$backfill.failures
    $backfillStatus = [string]$backfill.status

    if (-not $JsonOutput) {
        Write-Host ("   模型加载: " + $modelLoaded)
        Write-Host ("   熔断状态: is_open=" + $isBreakerOpen + " open_until=" + $breaker.open_until)
        Write-Host ("   限流状态: limit=" + $limiter.limit_per_minute + " used=" + $limiter.current_requests + " remaining=" + $remaining)
        Write-Host ("   回填状态: status=" + $backfillStatus + " rounds=" + $backfill.rounds + " rows=" + $backfill.rows + " failures=" + $backfillFailures)
    }

    if (-not $JsonOutput) {
        Write-Host "2) 检查检索审计日志（GET /ai/search-audit-logs）..."
    }
    $audit = Invoke-AiApi "GET" ("/ai/search-audit-logs?limit=" + $AuditLimit)
    if ($audit.code -ne 0) {
        throw ("检索审计日志查询失败：" + $audit.msg)
    }

    $items = @()
    if ($null -ne $audit.data -and $null -ne $audit.data.items) {
        $items = @($audit.data.items)
    }
    $highLatency = @($items | Where-Object { [double]$_.latency_ms -gt $MaxLatencyMs })
    $failedItems = @($items | Where-Object { -not (ConvertTo-Boolean $_.success) })

    if (-not $JsonOutput) {
        Write-Host ("   审计缓存总数: " + $audit.data.total_cached)
        Write-Host ("   本次返回条数: " + $audit.data.returned)
        Write-Host ("   慢请求条数(>" + $MaxLatencyMs + "ms): " + $highLatency.Count)
        Write-Host ("   失败请求条数: " + $failedItems.Count)
    }

    $issues = @()
    if ($isBreakerOpen) {
        $issues += "熔断处于打开状态"
    }
    if ($remaining -lt $MinRemainingQuota) {
        $issues += ("限流剩余额度过低(remaining=" + $remaining + ", threshold=" + $MinRemainingQuota + ")")
    }
    if ($failedItems.Count -gt 0) {
        $issues += ("审计日志存在失败请求(count=" + $failedItems.Count + ")")
    }
    if ($highLatency.Count -gt 0) {
        $issues += ("审计日志存在慢请求(count=" + $highLatency.Count + ", threshold_ms=" + $MaxLatencyMs + ")")
    }
    if ($BackfillFailureAsError -and $backfillFailures -gt 0) {
        $issues += ("回填失败次数大于0(failures=" + $backfillFailures + ")")
    }

    $metrics = @{
        model_loaded = $modelLoaded
        breaker_open = $isBreakerOpen
        breaker_open_until = $breaker.open_until
        rate_limit_per_minute = [int]$limiter.limit_per_minute
        rate_limit_current_requests = [int]$limiter.current_requests
        rate_limit_remaining = $remaining
        backfill_status = $backfillStatus
        backfill_rounds = [int]$backfill.rounds
        backfill_rows = [int]$backfill.rows
        backfill_failures = $backfillFailures
        audit_total_cached = [int]$audit.data.total_cached
        audit_returned = [int]$audit.data.returned
        audit_failed_count = $failedItems.Count
        audit_high_latency_count = $highLatency.Count
        thresholds = @{
            max_latency_ms = $MaxLatencyMs
            min_remaining_quota = $MinRemainingQuota
            backfill_failure_as_error = [bool]$BackfillFailureAsError
        }
    }

    if ($issues.Count -gt 0) {
        Write-CheckResult -ExitCode 2 -State "not_ready" -Message "AI 检索巡检未通过" -Issues $issues -Metrics $metrics -JsonOnly:$JsonOutput
    }
    Write-CheckResult -ExitCode 0 -State "ready" -Message "AI 检索巡检通过。" -Issues @() -Metrics $metrics -JsonOnly:$JsonOutput
}
catch {
    $metrics = @{
        model_loaded = $false
        breaker_open = $false
        rate_limit_remaining = -1
        backfill_failures = -1
        audit_returned = 0
    }
    Write-CheckResult -ExitCode 3 -State "error" -Message $_.Exception.Message -Issues @($_.Exception.Message) -Metrics $metrics -JsonOnly:$JsonOutput
}
