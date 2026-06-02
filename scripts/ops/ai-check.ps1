# File: AI Retrieval Ops Check Script
# Usage: check AI health, guard state, and search audit logs.
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

function ConvertTo-Boolean($Value) {
    if ($null -eq $Value) { return $false }
    return [bool]$Value
}

function Get-OptionalProperty($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    if ($null -eq $property.Value) { return $Default }
    return $property.Value
}

function ConvertTo-Int($Value, [int]$Default = 0) {
    if ($null -eq $Value) { return $Default }
    try { return [int]$Value } catch { return $Default }
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
        Write-Host "1) Check AI health (GET /)..."
    }
    $health = Invoke-AiApi "GET" "/"
    if ($health.code -ne 0) {
        throw ("AI health check failed: " + $health.msg)
    }

    $modelLoaded = ConvertTo-Boolean $health.model_loaded
    $guard = Get-OptionalProperty $health "retrieval_guard"
    $breaker = Get-OptionalProperty $guard "circuit_breaker"
    $limiter = Get-OptionalProperty $guard "rate_limiter"
    $backfill = Get-OptionalProperty $health "backfill_state"

    if (-not $modelLoaded) {
        throw "AI model is not loaded. Check AI service model initialization logs first."
    }
    if ($null -eq $breaker) { $breaker = [pscustomobject]@{} }
    if ($null -eq $limiter) { $limiter = [pscustomobject]@{} }
    if ($null -eq $backfill) { $backfill = [pscustomobject]@{} }

    $isBreakerOpen = ConvertTo-Boolean (Get-OptionalProperty $breaker "is_open")
    $remaining = ConvertTo-Int (Get-OptionalProperty $limiter "remaining") 0
    $backfillFailures = ConvertTo-Int (Get-OptionalProperty $backfill "failures") 0
    $backfillStatus = [string](Get-OptionalProperty $backfill "status" "")

    if (-not $JsonOutput) {
        Write-Host ("   model_loaded: " + $modelLoaded)
        Write-Host ("   breaker: is_open=" + $isBreakerOpen + " open_until=" + (Get-OptionalProperty $breaker "open_until" ""))
        Write-Host ("   limiter: limit=" + (Get-OptionalProperty $limiter "limit_per_minute" "") + " used=" + (Get-OptionalProperty $limiter "current_requests" "") + " remaining=" + $remaining)
        Write-Host ("   backfill: status=" + $backfillStatus + " rounds=" + (Get-OptionalProperty $backfill "rounds" "") + " rows=" + (Get-OptionalProperty $backfill "rows" "") + " failures=" + $backfillFailures)
    }

    if (-not $JsonOutput) {
        Write-Host "2) Check search audit logs (GET /ai/search-audit-logs)..."
    }
    $audit = Invoke-AiApi "GET" ("/ai/search-audit-logs?limit=" + $AuditLimit)
    if ($audit.code -ne 0) {
        throw ("Search audit log query failed: " + $audit.msg)
    }

    $items = @()
    if ($null -ne $audit.data -and $null -ne $audit.data.items) {
        $items = @($audit.data.items)
    }
    $highLatency = @($items | Where-Object { [double]$_.latency_ms -gt $MaxLatencyMs })
    $failedItems = @($items | Where-Object { -not (ConvertTo-Boolean $_.success) })

    if (-not $JsonOutput) {
        Write-Host ("   audit_total_cached: " + $audit.data.total_cached)
        Write-Host ("   audit_returned: " + $audit.data.returned)
        Write-Host ("   high_latency_count(>" + $MaxLatencyMs + "ms): " + $highLatency.Count)
        Write-Host ("   failed_request_count: " + $failedItems.Count)
    }

    $issues = @()
    if ($isBreakerOpen) {
        $issues += "Circuit breaker is open."
    }
    if ($remaining -lt $MinRemainingQuota) {
        $issues += ("Rate limit remaining quota is low (remaining=" + $remaining + ", threshold=" + $MinRemainingQuota + ").")
    }
    if ($failedItems.Count -gt 0) {
        $issues += ("Search audit logs contain failed requests (count=" + $failedItems.Count + ").")
    }
    if ($highLatency.Count -gt 0) {
        $issues += ("Search audit logs contain slow requests (count=" + $highLatency.Count + ", threshold_ms=" + $MaxLatencyMs + ").")
    }
    if ($BackfillFailureAsError -and $backfillFailures -gt 0) {
        $issues += ("Backfill failures are greater than 0 (failures=" + $backfillFailures + ").")
    }

    $metrics = @{
        model_loaded = $modelLoaded
        breaker_open = $isBreakerOpen
        breaker_open_until = Get-OptionalProperty $breaker "open_until" ""
        rate_limit_per_minute = ConvertTo-Int (Get-OptionalProperty $limiter "limit_per_minute") 0
        rate_limit_current_requests = ConvertTo-Int (Get-OptionalProperty $limiter "current_requests") 0
        rate_limit_remaining = $remaining
        backfill_status = $backfillStatus
        backfill_rounds = ConvertTo-Int (Get-OptionalProperty $backfill "rounds") 0
        backfill_rows = ConvertTo-Int (Get-OptionalProperty $backfill "rows") 0
        backfill_failures = $backfillFailures
        audit_total_cached = ConvertTo-Int $audit.data.total_cached 0
        audit_returned = ConvertTo-Int $audit.data.returned 0
        audit_failed_count = $failedItems.Count
        audit_high_latency_count = $highLatency.Count
        thresholds = @{
            max_latency_ms = $MaxLatencyMs
            min_remaining_quota = $MinRemainingQuota
            backfill_failure_as_error = [bool]$BackfillFailureAsError
        }
    }

    if ($issues.Count -gt 0) {
        Write-CheckResult -ExitCode 2 -State "not_ready" -Message "AI retrieval ops check failed." -Issues $issues -Metrics $metrics -JsonOnly:$JsonOutput
    }
    Write-CheckResult -ExitCode 0 -State "ready" -Message "AI retrieval ops check passed." -Issues @() -Metrics $metrics -JsonOnly:$JsonOutput
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
