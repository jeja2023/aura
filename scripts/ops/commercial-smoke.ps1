param(
    [string]$BaseUrl = "http://127.0.0.1:5099",
    [long]$TenantId = 0,
    [string]$EnvironmentFile = ".env.docker"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$environmentPath = if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) { $EnvironmentFile } else { Join-Path $repositoryRoot $EnvironmentFile }
$values = @{}
Get-Content -LiteralPath $environmentPath -Encoding utf8 | ForEach-Object {
    if ($_ -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $values[$matches[1]] = $matches[2].Trim().Trim('"').Trim("'")
    }
}
$password = $values["AURA_ADMIN_PASSWORD"]
$userName = if ($values["AURA_ADMIN_USER"]) { $values["AURA_ADMIN_USER"] } else { "admin" }
if ([string]::IsNullOrWhiteSpace($password)) { throw "AURA_ADMIN_PASSWORD is required in $environmentPath" }
$script:webSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Add-Type -AssemblyName System.Net.Http

function Invoke-AuraApi {
    param([string]$Method, [string]$Path, $Body = $null, [hashtable]$ExtraHeaders = @{})
    $headers = @{}
    foreach ($item in $ExtraHeaders.GetEnumerator()) { $headers[$item.Key] = $item.Value }
    $parameters = @{
        Method = $Method
        Uri = "$BaseUrl$Path"
        Headers = $headers
        TimeoutSec = 30
        WebSession = $script:webSession
    }
    if ($null -ne $Body) {
        $parameters["Body"] = $Body | ConvertTo-Json -Compress -Depth 20
        $parameters["ContentType"] = "application/json; charset=utf-8"
    }
    try {
        $response = Invoke-RestMethod @parameters
    }
    catch {
        $detail = $_.Exception.Message
        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $bodyText = $reader.ReadToEnd()
                if ($bodyText) { $detail = $bodyText }
            }
            catch { }
        }
        throw "$Method $Path failed: $detail"
    }
    if ($null -ne $response.code -and $response.code -ne 0) {
        throw "$Method $Path returned code $($response.code): $($response.msg)"
    }
    return $response
}

function Invoke-AuraPhotoUpload {
    param([long]$TenantId, [long]$CaseId)
    $baseUri = [Uri]$BaseUrl
    $authCookie = $script:webSession.Cookies.GetCookies($baseUri)["aura_token"]
    if ($null -eq $authCookie) { throw "Photo upload requires an authenticated Aura cookie" }
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = $null
    $form = $null
    try {
        $handler.CookieContainer.Add($baseUri, [System.Net.Cookie]::new("aura_token", $authCookie.Value, "/", $baseUri.Host))
        $client = [System.Net.Http.HttpClient]::new($handler)
        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $png = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
        $image = [System.Net.Http.ByteArrayContent]::new($png)
        $image.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("image/png")
        $form.Add($image, "file", "commercial-smoke.png")
        $uri = "$BaseUrl/api/v1/mobile/cases/$CaseId/photos?tenantId=$TenantId&purpose=commercial-smoke&latitude=31.2304&longitude=121.4737"
        $response = $client.PostAsync($uri, $form).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Photo upload failed: $($response.StatusCode) $text" }
        $payload = $text | ConvertFrom-Json
        if ($payload.code -ne 0 -or [long]$payload.data.evidenceId -le 0 -or $payload.data.locationRecorded -ne $true) {
            throw "Photo upload returned invalid evidence metadata: $text"
        }
        return $payload
    }
    finally {
        if ($form) { $form.Dispose() }
        if ($client) { $client.Dispose() }
        $handler.Dispose()
    }
}

$runId = [Guid]::NewGuid().ToString("N")
$results = [System.Collections.Generic.List[string]]::new()
$login = Invoke-AuraApi "POST" "/api/auth/login" @{ userName = $userName; password = $password }
$authCookie = $script:webSession.Cookies.GetCookies([Uri]$BaseUrl)["aura_token"]
if ($null -eq $authCookie) { throw "Login returned no authentication cookie" }
$results.Add("login")

$workbench = Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/workbench/" -WebSession $script:webSession -TimeoutSec 30
if ($workbench.StatusCode -ne 200 -or $workbench.Content -notmatch 'id="tenantScope"') {
    throw "Authenticated workbench route did not return the expected application shell"
}
foreach ($asset in @("/workbench/workbench.css", "/workbench/workbench.js", "/workbench/manifest.webmanifest", "/workbench/sw.js")) {
    $assetResponse = Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl$asset" -WebSession $script:webSession -TimeoutSec 30
    if ($assetResponse.StatusCode -ne 200 -or $assetResponse.RawContentLength -le 0) { throw "Workbench asset failed: $asset" }
}
$results.Add("workbench-static-shell")

$tenants = Invoke-AuraApi "GET" "/api/tenant/list?limit=200"
$selectedTenant = if ($TenantId -gt 0) { $tenants.data | Where-Object { $_.tenantId -eq $TenantId } | Select-Object -First 1 } else { $tenants.data | Select-Object -First 1 }
if ($null -eq $selectedTenant) {
    $createdTenant = Invoke-AuraApi "POST" "/api/tenant/project" @{
        tenantCode = "smoke-$($runId.Substring(0, 12))"
        tenantName = "Commercial smoke tenant"
        configJson = "{}"
        enabled = $true
    }
    $TenantId = [long]$createdTenant.data.tenantId
} else {
    $TenantId = [long]$selectedTenant.tenantId
}
$results.Add("tenant")

[void](Invoke-AuraApi "GET" "/api/v1/release-governance/capabilities?productVersion=0.3.0")
[void](Invoke-AuraApi "GET" "/api/v1/identity/oidc/providers?tenantId=$TenantId")
[void](Invoke-AuraApi "GET" "/api/v1/identity/break-glass")
$results.Add("identity-and-capability-catalog")

$event = Invoke-AuraApi "POST" "/api/v1/events" @{
    tenantId = $TenantId
    eventType = "commercial_smoke"
    title = "Commercial smoke $runId"
    summary = "Automated runtime verification"
    severity = "high"
    aggregationKey = "commercial-smoke-$runId"
    aggregationPolicyVersion = 1
    occurredAt = [DateTimeOffset]::UtcNow.ToString("o")
    ruleCode = $null
    ruleVersion = $null
    modelCode = $null
    modelVersion = $null
    entityRef = "smoke-person-$runId"
    spaceRef = "smoke-space"
    representativeEvidence = @{ source = "commercial-smoke" }
    analysisEventId = $null
} @{ "Idempotency-Key" = "event-$runId" }
$eventId = [long]$event.data.eventId
if ($eventId -le 0) { throw "Event create returned no eventId" }
[void](Invoke-AuraApi "GET" "/api/v1/events/$eventId`?tenantId=$TenantId")
[void](Invoke-AuraApi "POST" "/api/v1/rules/0/evaluate" @{ tenantId = $TenantId; eventId = $eventId })
$results.Add("event-and-rule-evaluation")

$case = Invoke-AuraApi "POST" "/api/v1/cases" @{
    tenantId = $TenantId
    title = "Commercial smoke case $runId"
    description = "Runtime verification case"
    priority = "high"
    ownerUserId = $null
    ownerName = $userName
    eventIds = @($eventId)
    tags = @("smoke")
    acknowledgeDueAt = $null
    startDueAt = $null
    resolveDueAt = [DateTimeOffset]::UtcNow.AddHours(4).ToString("o")
} @{ "Idempotency-Key" = "case-$runId" }
$caseId = [long]$case.data.caseId
if ($caseId -le 0) { throw "Case create returned no caseId" }
[void](Invoke-AuraApi "POST" "/api/v1/cases/$caseId/comments?tenantId=$TenantId" @{ content = "Commercial smoke comment"; visibility = "team" })
[void](Invoke-AuraApi "GET" "/api/v1/cases/$caseId`?tenantId=$TenantId")
$results.Add("case-and-comment")

$users = Invoke-AuraApi "GET" "/api/user/list?page=1&pageSize=20&keyword=$([Uri]::EscapeDataString($userName))"
$operator = $users.data | Where-Object { $_.userName -eq $userName } | Select-Object -First 1
if ($null -eq $operator -or [long]$operator.userId -le 0) { throw "Smoke operator has no stable user ID" }
[void](Invoke-AuraApi "POST" "/api/v1/cases/$caseId/participants" @{
    tenantId = $TenantId; userId = [long]$operator.userId; roleType = "assignee"
})
$template = Invoke-AuraApi "POST" "/api/v1/case-templates" @{
    tenantId = $TenantId
    templateCode = "smoke_$runId"
    version = 1
    name = "Commercial smoke template"
    eventType = "commercial_smoke"
    defaultPriority = "high"
    defaultSla = @{ resolveHours = 4 }
    checklist = @(@{ code = "verify_scene"; title = "Verify scene"; required = $true })
    requiredEvidence = @(@{ type = "photo"; minimum = 1 })
}
$templateId = [long]$template.data.templateId
[void](Invoke-AuraApi "POST" "/api/v1/case-templates/$templateId/state" @{ tenantId = $TenantId; targetStatus = "active" })
[void](Invoke-AuraApi "POST" "/api/v1/cases/$caseId/templates/$templateId`?tenantId=$TenantId")
$checklist = Invoke-AuraApi "GET" "/api/v1/cases/$caseId/checklist?tenantId=$TenantId"
$checklistItem = $checklist.data | Select-Object -First 1
if ($null -eq $checklistItem) { throw "Case template produced no checklist item" }
[void](Invoke-AuraApi "POST" "/api/v1/cases/$caseId/checklist/$([long]$checklistItem.checklistItemId)" @{
    tenantId = $TenantId; status = "completed"; detail = @{ source = "commercial-smoke" }
})
[void](Invoke-AuraApi "GET" "/api/v1/cases/$caseId/participants?tenantId=$TenantId")
$results.Add("case-collaboration-and-template")

$investigation = Invoke-AuraApi "POST" "/api/v1/investigations" @{ tenantId = $TenantId; title = "Smoke investigation $runId" }
$investigationId = [long]$investigation.data.investigationId
[void](Invoke-AuraApi "POST" "/api/v1/investigations/$investigationId/queries?tenantId=$TenantId" @{
    queryType = "timeline"
    query = @{ eventId = $eventId }
    modelCode = $null
    modelVersion = $null
    thresholdPolicyVersion = $null
    dataVersion = "current"
})
$results.Add("investigation")

$controlled = Invoke-AuraApi "POST" "/api/v1/controlled-queries" @{
    tenantId = $TenantId
    investigationId = $investigationId
    text = "timeline 2026-07-01 2026-07-02"
}
$queryPlanId = [long]$controlled.data.queryPlanId
[void](Invoke-AuraApi "PUT" "/api/v1/controlled-queries/$queryPlanId/plan" @{
    tenantId = $TenantId
    plan = @{ queryType = "timeline"; query = @{ from = "2026-07-01"; to = "2026-07-02"; limit = 25 } }
})
[void](Invoke-AuraApi "POST" "/api/v1/controlled-queries/$queryPlanId/confirm" @{ tenantId = $TenantId; confirm = $true })
[void](Invoke-AuraApi "POST" "/api/v1/controlled-queries/$queryPlanId/execute?tenantId=$TenantId")
[void](Invoke-AuraApi "POST" "/api/v1/controlled-queries/safety-evaluations?tenantId=$TenantId")
$results.Add("controlled-query-plan-and-safety")

$onboarding = Invoke-AuraApi "POST" "/api/v1/integrations/onboarding" @{ tenantId = $TenantId; integrationType = "standard_http"; name = "Smoke provider $runId" }
$onboardingId = [long]$onboarding.data.onboardingId
[void](Invoke-AuraApi "POST" "/api/v1/integrations/onboarding/$onboardingId/steps?tenantId=$TenantId" @{
    step = 1
    config = @{ baseUrl = "https://provider.invalid"; mode = "contract-only" }
    secretReferences = @{ token = "env://SMOKE_PROVIDER_TOKEN" }
    runTest = $false
    exemptionReason = $null
})
$results.Add("integration-onboarding")

$rule = Invoke-AuraApi "POST" "/api/v1/governance/rules" @{ tenantId = $TenantId; payload = @{ ruleCode = "smoke_$runId"; name = "Smoke rule $runId" } }
$ruleId = [long]$rule.data.id
[void](Invoke-AuraApi "POST" "/api/v1/governance/rule-versions" @{ tenantId = $TenantId; payload = @{
    ruleId = $ruleId
    condition = @{ eventType = "commercial_smoke"; occurrenceMin = 1; startHour = 0; endHour = 23 }
    action = @{ createCase = $false }
    noiseControl = @{ suppressionMinutes = 0; maxTriggersPerHour = 100; keyBy = "entity" }
    rollout = @{ shadow = $true; percentage = 100 }
} })
[void](Invoke-AuraApi "POST" "/api/v1/rules/$ruleId/dry-run" @{ tenantId = $TenantId; version = 1; from = $null; to = $null; limit = 10000 })
$results.Add("rule-draft-and-dry-run")

[void](Invoke-AuraApi "POST" "/api/v1/analytics/events" @{
    tenantId = $TenantId
    eventName = "workbench.smoke"
    objectType = "case"
    objectId = "$caseId"
    properties = @{ source = "commercial-smoke"; outcome = "created" }
    sessionRef = $runId
})
$from = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(-1).ToString("o"))
$to = [Uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(1).ToString("o"))
[void](Invoke-AuraApi "GET" "/api/v1/analytics/dashboard?tenantId=$TenantId&from=$from&to=$to")
[void](Invoke-AuraApi "GET" "/api/v1/ai-governance/dashboard?tenantId=$TenantId&from=$from&to=$to")
$results.Add("analytics-and-ai-dashboard")

$draft = Invoke-AuraApi "POST" "/api/v1/mobile/drafts" @{
    tenantId = $TenantId
    clientDraftId = [Guid]::NewGuid()
    actionType = "case_comment"
    objectType = "case"
    objectId = "$caseId"
    baseVersion = $null
    payload = @{ content = "Synced from commercial smoke draft" }
    expiresAt = [DateTimeOffset]::UtcNow.AddHours(1).ToString("o")
}
$draftId = [long]$draft.data.mobileDraftId
[void](Invoke-AuraApi "POST" "/api/v1/mobile/drafts/$draftId/sync" @{ tenantId = $TenantId; currentVersion = $null })
$results.Add("mobile-draft-sync")

$mobileTasks = Invoke-AuraApi "GET" "/api/v1/mobile/tasks?tenantId=$TenantId"
if (-not ($mobileTasks.data.cases | Where-Object { [long]$_.caseId -eq $caseId })) { throw "Assigned case is absent from mobile tasks" }
$deepLink = Invoke-AuraApi "POST" "/api/v1/mobile/deep-links" @{ tenantId = $TenantId; objectType = "case"; objectId = "$caseId"; reason = "commercial-smoke" }
if ($deepLink.data.path -notmatch "caseId=$caseId") { throw "Mobile deep link did not preserve the case ID" }
[void](Invoke-AuraApi "GET" "/api/v1/mobile/push-config")
$results.Add("mobile-tasks-and-deep-link")
$photo = Invoke-AuraPhotoUpload -TenantId $TenantId -CaseId $caseId
$results.Add("mobile-photo-evidence")

[void](Invoke-AuraApi "GET" "/api/v1/ops/center?tenantId=$TenantId")
[void](Invoke-AuraApi "GET" "/api/v1/commercial/usage/report?tenantId=$TenantId")
$results.Add("operations-and-usage")

Write-Output "Commercial smoke passed: $($results.Count) groups."
Write-Output ($results -join ", ")
Write-Output "Created eventId=$eventId caseId=$caseId investigationId=$investigationId ruleId=$ruleId"
