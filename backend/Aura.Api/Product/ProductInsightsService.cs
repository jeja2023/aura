using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Dapper;

namespace Aura.Api.Product;

internal sealed partial class ProductInsightsService(
    PgSqlConnectionFactory connectionFactory,
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    IHostEnvironment environment,
    IConfiguration configuration)
{
    private static readonly string[] RequiredAdapterChecks =
        ["manifest_valid", "health", "discovery", "sample", "timeout", "error_mapping", "credentials", "ssrf"];

    public async Task<ProductCommandResult> RunAdapterContractAsync(AdapterContractRunRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceModel) || string.IsNullOrWhiteSpace(request.FirmwareVersion))
            return new(ProductCommandStatus.Invalid, Message: "deviceModel and firmwareVersion are required");
        var checkMap = request.Checks
            .Where(check => !string.IsNullOrWhiteSpace(check.Code))
            .GroupBy(check => check.Code.Trim().ToLowerInvariant())
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var missing = RequiredAdapterChecks.Where(code => !checkMap.ContainsKey(code)).ToArray();
        var realDevice = request.Environment.ValueKind == JsonValueKind.Object
            && request.Environment.TryGetProperty("realDevice", out var realDeviceNode)
            && realDeviceNode.ValueKind == JsonValueKind.True;
        var failed = request.Checks.Where(check => !string.Equals(check.Status, "passed", StringComparison.OrdinalIgnoreCase)).Select(check => check.Code).ToArray();
        var status = missing.Length > 0 || !realDevice || string.IsNullOrWhiteSpace(request.ReportUri)
            ? "blocked"
            : failed.Length > 0 ? "failed" : "passed";
        var checksJson = JsonSerializer.Serialize(new { required = RequiredAdapterChecks, missing, failed, supplied = request.Checks });
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var adapterExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM adapter_manifest WHERE adapter_id=@Id)", new { Id = request.AdapterId }, transaction, cancellationToken: cancellationToken));
        if (!adapterExists) return new(ProductCommandStatus.NotFound, Message: "Adapter manifest not found");
        var runId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO adapter_contract_run(adapter_id,device_model,firmware_version,environment_json,checks_json,status,report_uri,executed_by)
            VALUES(@AdapterId,@Device,@Firmware,@Environment::jsonb,@Checks::jsonb,@Status,@Report,@Actor)
            RETURNING contract_run_id
            """, new
            {
                request.AdapterId, Device = request.DeviceModel.Trim(), Firmware = request.FirmwareVersion.Trim(),
                Environment = request.Environment.GetRawText(), Checks = checksJson, Status = status,
                Report = Clean(request.ReportUri, 2000), Actor = actor
            }, transaction, cancellationToken: cancellationToken));
        if (status == "passed")
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adapter_certification(adapter_id,device_model,firmware_version,capabilities_json,limitations_json,report_uri,status,tested_at)
                VALUES(@AdapterId,@Device,@Firmware,@Capabilities::jsonb,'[]'::jsonb,@Report,'certified',CURRENT_TIMESTAMP)
                ON CONFLICT(adapter_id,device_model,firmware_version) DO UPDATE SET
                  capabilities_json=EXCLUDED.capabilities_json,report_uri=EXCLUDED.report_uri,status='certified',tested_at=CURRENT_TIMESTAMP;
                UPDATE adapter_manifest SET lifecycle_status='certified' WHERE adapter_id=@AdapterId;
                """, new
                {
                    request.AdapterId, Device = request.DeviceModel.Trim(), Firmware = request.FirmwareVersion.Trim(),
                    Capabilities = JsonSerializer.Serialize(request.Checks.Where(check => check.Status.Equals("passed", StringComparison.OrdinalIgnoreCase)).Select(check => check.Code)),
                    Report = request.ReportUri!.Trim()
                }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        var message = status == "blocked"
            ? "Certification is blocked until all checks, real-device evidence, and a report URI are present"
            : status == "failed" ? "Adapter contract checks failed" : "Adapter certification passed";
        return ProductCommandResult.Ok(new { contractRunId = runId, status, missing, failed, realDevice }, message);
    }

    public async Task<object> GetBusinessDashboardAsync(long tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) throw new ArgumentException("to must be after from");
        if (to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Dashboard window cannot exceed 366 days");
        await using var connection = connectionFactory.CreateConnection();
        var summary = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(*) FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To) AS EventCount,
              (SELECT COUNT(DISTINCT COALESCE(NULLIF(aggregation_key,''),event_no)) FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To) AS IndependentEventCount,
              (SELECT COUNT(*) FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND occurrence_count>1) AS RepeatedEventCount,
              (SELECT COUNT(*) FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND status='dismissed') AS DismissedEventCount,
              (SELECT COUNT(*) FROM incident_case WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To) AS CaseCount,
              (SELECT COUNT(*) FROM incident_case WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND status IN ('resolved','closed')) AS ClosedCaseCount,
              (SELECT COUNT(*) FROM incident_case WHERE tenant_id=@TenantId AND status NOT IN ('resolved','closed','false_positive')) AS OpenBacklog,
              (SELECT COUNT(*) FROM incident_case WHERE tenant_id=@TenantId AND closed_at>=@From AND closed_at<@To AND closed_at<=resolve_due_at) AS SlaMetCount,
              (SELECT COUNT(*) FROM incident_case WHERE tenant_id=@TenantId AND closed_at>=@From AND closed_at<@To AND resolve_due_at IS NOT NULL) AS SlaEligibleCount,
              (SELECT COUNT(*) FROM ai_human_feedback WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To) AS FeedbackCount,
              (SELECT COUNT(*) FROM ai_human_feedback WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND feedback_type='false_positive') AS FalsePositiveFeedbackCount,
              (SELECT COUNT(*) FROM incident_case_evidence WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To) AS EvidenceCount
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var trend = await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            WITH days AS (SELECT generate_series(date_trunc('day',@From::timestamptz),date_trunc('day',@To::timestamptz),INTERVAL '1 day') AS day),
            events AS (SELECT date_trunc('day',created_at) AS day,COUNT(*) AS count FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To GROUP BY 1),
            cases AS (SELECT date_trunc('day',created_at) AS day,COUNT(*) AS count FROM incident_case WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To GROUP BY 1)
            SELECT d.day AS Day,COALESCE(e.count,0) AS EventCount,COALESCE(c.count,0) AS CaseCount
            FROM days d LEFT JOIN events e ON e.day=d.day LEFT JOIN cases c ON c.day=d.day ORDER BY d.day
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var hotSpots = await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT COALESCE(space_ref,'unassigned') AS SpaceRef,COUNT(*) AS EventCount,
              COUNT(*) FILTER(WHERE severity IN ('high','critical')) AS HighSeverityCount
            FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To
            GROUP BY COALESCE(space_ref,'unassigned') ORDER BY EventCount DESC LIMIT 20
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var ruleDistribution = await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT COALESCE(rule_code,'unattributed') AS RuleCode,COUNT(*) AS EventCount
            FROM business_event WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To
            GROUP BY COALESCE(rule_code,'unattributed') ORDER BY EventCount DESC LIMIT 20
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var caseTiming = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(
            """
            SELECT
              COALESCE(AVG(EXTRACT(EPOCH FROM (acknowledged_at-created_at))) FILTER(WHERE acknowledged_at IS NOT NULL),0) AS AverageAcknowledgeSeconds,
              COALESCE(AVG(EXTRACT(EPOCH FROM (started_at-created_at))) FILTER(WHERE started_at IS NOT NULL),0) AS AverageStartSeconds,
              COALESCE(AVG(EXTRACT(EPOCH FROM (closed_at-created_at))) FILTER(WHERE closed_at IS NOT NULL),0) AS AverageCloseSeconds
            FROM incident_case WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var quality = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(DISTINCT case_id) FROM incident_case_activity WHERE tenant_id=@TenantId
                AND created_at>=@From AND created_at<@To AND activity_type IN ('reopened','transitioned') AND to_status='reopened') AS ReopenedCaseCount,
              (SELECT COUNT(*) FROM incident_case WHERE tenant_id=@TenantId AND closed_at>=@From AND closed_at<@To) AS ClosedCaseDenominator,
              (SELECT COUNT(*) FROM incident_case c WHERE c.tenant_id=@TenantId AND c.closed_at>=@From AND c.closed_at<@To
                AND EXISTS(SELECT 1 FROM incident_case_evidence e WHERE e.case_id=c.case_id)) AS EvidenceCompleteCaseCount,
              (SELECT COUNT(*) FROM incident_case_relation WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND relation_type='merged_into') AS DuplicateCaseCount,
              (SELECT COUNT(*) FROM ai_human_feedback WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND review_status IN ('accepted','rejected')) AS ReviewedFeedbackCount,
              (SELECT COUNT(*) FROM ai_human_feedback WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND review_status='accepted') AS AcceptedFeedbackCount
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var aiQuality = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(*) FROM ai_evaluation_run WHERE tenant_id=@TenantId AND completed_at>=@From AND completed_at<@To) AS EvaluationCount,
              (SELECT COUNT(*) FROM ai_evaluation_run WHERE tenant_id=@TenantId AND completed_at>=@From AND completed_at<@To AND status='passed') AS PassedEvaluationCount,
              (SELECT COUNT(*) FROM ai_evaluation_item item JOIN ai_evaluation_run run ON run.evaluation_run_id=item.evaluation_run_id
                WHERE run.tenant_id=@TenantId AND item.created_at>=@From AND item.created_at<@To AND item.error_category='empty_result') AS EmptyResultCount,
              (SELECT COUNT(*) FROM ai_evaluation_item item JOIN ai_evaluation_run run ON run.evaluation_run_id=item.evaluation_run_id
                WHERE run.tenant_id=@TenantId AND item.created_at>=@From AND item.created_at<@To) AS EvaluationItemCount,
              (SELECT COUNT(*) FROM ai_drift_snapshot WHERE tenant_id=@TenantId AND window_end>=@From AND window_start<@To AND status<>'normal') AS DriftAlertCount
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var latestEvaluations = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT r.evaluation_run_id AS evaluationRunId,m.model_code AS modelCode,m.model_version AS modelVersion,
              r.status,r.metrics_json AS metrics,r.completed_at AS completedAt
            FROM ai_evaluation_run r JOIN ai_model_release m ON m.model_release_id=r.model_release_id
            WHERE r.tenant_id=@TenantId AND r.created_at>=@From AND r.created_at<@To
            ORDER BY r.created_at DESC LIMIT 20
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var platform = await connection.QuerySingleAsync<dynamic>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(*) FROM media_analysis_job WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To) AS ProviderJobCount,
              (SELECT COUNT(*) FROM media_analysis_job WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND status='completed') AS ProviderJobCompletedCount,
              (SELECT COUNT(*) FROM media_analysis_job WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND status IN ('completed','failed','cancelled','rejected')) AS ProviderJobTerminalCount,
              (SELECT COUNT(*) FROM media_analysis_job WHERE tenant_id=@TenantId AND status IN ('pending','submitting','accepted','running','retry_wait')) AS ProviderJobBacklog,
              (SELECT COUNT(*) FROM integration_outbox WHERE tenant_id=@TenantId AND status='dead_letter') AS OutboxDeadLetters,
              (SELECT COUNT(*) FROM media_analysis_inbox WHERE tenant_id=@TenantId AND status='dead_letter') AS InboxDeadLetters,
              (SELECT COUNT(*) FROM notification_delivery WHERE tenant_id=@TenantId AND status IN ('failed','dead_letter')) AS NotificationFailures,
              (SELECT COALESCE(AVG(EXTRACT(EPOCH FROM (completed_at-created_at))),0) FROM media_analysis_job
                WHERE tenant_id=@TenantId AND created_at>=@From AND created_at<@To AND completed_at IS NOT NULL) AS AverageProviderLatencySeconds
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var usage = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT metric_code AS metricCode,unit,SUM(quantity) AS quantity,COUNT(*) AS entries
            FROM tenant_usage_ledger WHERE tenant_id=@TenantId AND occurred_at>=@From AND occurred_at<@To
            GROUP BY metric_code,unit ORDER BY metric_code,unit
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var costs = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT cost_category AS costCategory,currency,SUM(amount) AS amount,COUNT(*) AS entries
            FROM tenant_cost_ledger WHERE tenant_id=@TenantId AND occurred_at>=@From AND occurred_at<@To
            GROUP BY cost_category,currency ORDER BY cost_category,currency
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        var definitions = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT DISTINCT ON(metric_code) metric_code AS metricCode,version,name,
              numerator_definition AS numeratorDefinition,denominator_definition AS denominatorDefinition,
              window_definition AS windowDefinition,effective_at AS effectiveAt
            FROM product_metric_definition WHERE effective_at<=@To ORDER BY metric_code,effective_at DESC,version DESC
            """, new { To = to }, cancellationToken: cancellationToken));
        var rates = new
        {
            slaCompliance = Ratio(summary.slametcount, summary.slaeligiblecount, from, to, "case_sla_compliance", 1),
            evidenceCompleteness = Ratio(quality.evidencecompletecasecount, quality.closedcasedenominator, from, to, "case_evidence_completeness", 1),
            reopenRate = Ratio(quality.reopenedcasecount, quality.closedcasedenominator, from, to, "case_reopen_rate", 1),
            falsePositiveFeedback = Ratio(summary.falsepositivefeedbackcount, summary.feedbackcount, from, to, "feedback_false_positive_rate", 1),
            providerJobSuccess = Ratio(platform.providerjobcompletedcount, platform.providerjobterminalcount, from, to, "provider_job_success_rate", 1)
        };
        return new
        {
            tenantId, from, to, summary, rates, caseTiming, quality, aiQuality, latestEvaluations,
            platform, resources = new { usage, costs }, trend, hotSpots, ruleDistribution,
            metricDefinitions = definitions, definitionsVersion = "1.0", generatedAt = DateTimeOffset.UtcNow
        };
    }

    private static object Ratio(object? numeratorValue, object? denominatorValue, DateTimeOffset from, DateTimeOffset to, string metricCode, int version)
    {
        var numerator = Convert.ToDecimal(numeratorValue ?? 0);
        var denominator = Convert.ToDecimal(denominatorValue ?? 0);
        return new { metricCode, version, numerator, denominator, value = denominator == 0 ? (decimal?)null : numerator / denominator, window = new { from, to } };
    }

    public async Task<ProductCommandResult> RecordAnalyticsAsync(AnalyticsEventRequest request, string actor, CancellationToken cancellationToken)
    {
        var eventName = request.EventName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AnalyticsNamePattern().IsMatch(eventName)) return new(ProductCommandStatus.Invalid, Message: "eventName is invalid");
        var properties = request.Properties?.GetRawText() ?? "{}";
        if (properties.Length > 16_384) return new(ProductCommandStatus.Invalid, Message: "analytics properties are too large");
        if (properties.Contains("base64", StringComparison.OrdinalIgnoreCase) || properties.Contains("image", StringComparison.OrdinalIgnoreCase))
            return new(ProductCommandStatus.Invalid, Message: "Raw media is not accepted in product analytics");
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO product_analytics_event(tenant_id,event_name,object_type,object_id,properties_json,actor,session_ref)
            VALUES(@TenantId,@Name,@ObjectType,@ObjectId,@Properties::jsonb,@Actor,@SessionRef) RETURNING analytics_event_id
            """, new
            {
                request.TenantId, Name = eventName, ObjectType = Clean(request.ObjectType, 64), ObjectId = Clean(request.ObjectId, 128),
                Properties = properties, Actor = actor, SessionRef = Clean(request.SessionRef, 128)
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { analyticsEventId = id });
    }

    public async Task<ProductCommandResult> SaveDraftAsync(MobileDraftWriteRequest request, string userName, CancellationToken cancellationToken)
    {
        if (request.ClientDraftId == Guid.Empty || string.IsNullOrWhiteSpace(request.ActionType) || string.IsNullOrWhiteSpace(request.ObjectType))
            return new(ProductCommandStatus.Invalid, Message: "clientDraftId, actionType and objectType are required");
        if (request.Payload.GetRawText().Length > 128_000) return new(ProductCommandStatus.Invalid, Message: "Draft payload is too large");
        var expires = request.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(7);
        expires = expires > DateTimeOffset.UtcNow.AddDays(7) ? DateTimeOffset.UtcNow.AddDays(7) : expires;
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO mobile_action_draft(tenant_id,user_name,client_draft_id,action_type,object_type,object_id,base_version,payload_json,expires_at)
            VALUES(@TenantId,@User,@ClientId,@Action,@ObjectType,@ObjectId,@BaseVersion,@Payload::jsonb,@Expires)
            ON CONFLICT(tenant_id,user_name,client_draft_id) DO UPDATE SET
              action_type=EXCLUDED.action_type,object_type=EXCLUDED.object_type,object_id=EXCLUDED.object_id,
              base_version=EXCLUDED.base_version,payload_json=EXCLUDED.payload_json,expires_at=EXCLUDED.expires_at,
              status='draft',conflict_json=NULL,updated_at=CURRENT_TIMESTAMP
            RETURNING mobile_draft_id
            """, new
            {
                request.TenantId, User = userName, ClientId = request.ClientDraftId, Action = request.ActionType.Trim().ToLowerInvariant(),
                ObjectType = request.ObjectType.Trim().ToLowerInvariant(), ObjectId = Clean(request.ObjectId, 128), request.BaseVersion,
                Payload = request.Payload.GetRawText(), Expires = expires
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { mobileDraftId = id, request.ClientDraftId, status = "draft", expiresAt = expires });
    }

    public async Task<object> GetMobileTasksAsync(long tenantId, string userName, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var cases = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT DISTINCT c.case_id AS caseId,c.case_no AS caseNo,c.title,c.status,c.priority,
              c.resolve_due_at AS resolveDueAt,c.version,c.updated_at AS updatedAt
            FROM incident_case c
            LEFT JOIN incident_case_participant p ON p.case_id=c.case_id AND p.removed_at IS NULL
            LEFT JOIN sys_user participant_user ON participant_user.user_id=p.user_id
            LEFT JOIN sys_user owner_user ON owner_user.user_id=c.owner_user_id
            WHERE c.tenant_id=@TenantId AND c.status NOT IN ('closed','false_positive')
              AND (owner_user.user_name=@User OR participant_user.user_name=@User OR c.owner_name=@User)
            ORDER BY c.updated_at DESC LIMIT 100
            """, new { TenantId = tenantId, User = userName }, cancellationToken: cancellationToken));
        var events = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT e.event_id AS eventId,e.event_no AS eventNo,e.title,e.status,e.severity,e.version,
              e.last_occurred_at AS lastOccurredAt,e.updated_at AS updatedAt
            FROM business_event e LEFT JOIN sys_user u ON u.user_id=e.triage_user_id
            WHERE e.tenant_id=@TenantId AND e.status IN ('open','acknowledged')
              AND (u.user_name=@User OR e.triage_user_name=@User)
            ORDER BY e.last_occurred_at DESC LIMIT 100
            """, new { TenantId = tenantId, User = userName }, cancellationToken: cancellationToken));
        return new { tenantId, userName, cases, events, generatedAt = DateTimeOffset.UtcNow };
    }

    public async Task<ProductCommandResult> SavePushSubscriptionAsync(
        MobilePushSubscriptionRequest request,
        string userName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EndpointUri) || request.EndpointUri.Length > 4000
            || string.IsNullOrWhiteSpace(request.KeyP256dh) || request.KeyP256dh.Length > 1024
            || string.IsNullOrWhiteSpace(request.KeyAuth) || request.KeyAuth.Length > 512)
            return new(ProductCommandStatus.Invalid, Message: "A valid Web Push subscription is required");
        Uri endpoint;
        try { endpoint = await outboundUrlPolicy.ValidateAsync(request.EndpointUri, cancellationToken); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return new(ProductCommandStatus.Invalid, Message: $"Push endpoint rejected: {ex.Message}"); }
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO mobile_push_subscription(tenant_id,user_name,endpoint_uri,key_p256dh,key_auth,user_agent,status)
            VALUES(@TenantId,@User,@Endpoint,@P256dh,@Auth,@UserAgent,'active')
            ON CONFLICT(tenant_id,user_name,endpoint_uri) DO UPDATE SET key_p256dh=EXCLUDED.key_p256dh,
              key_auth=EXCLUDED.key_auth,user_agent=EXCLUDED.user_agent,status='active',updated_at=CURRENT_TIMESTAMP
            RETURNING subscription_id
            """, new
            {
                request.TenantId, User = userName, Endpoint = endpoint.ToString(), P256dh = request.KeyP256dh.Trim(),
                Auth = request.KeyAuth.Trim(), UserAgent = Clean(request.UserAgent, 512)
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { subscriptionId = id, endpointHost = endpoint.Host, status = "active" });
    }

    public async Task<IReadOnlyList<object>> ListPushSubscriptionsAsync(long tenantId, string userName, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT subscription_id AS subscriptionId,
              regexp_replace(endpoint_uri,'^(https?://[^/]+).*$',E'\\1/...') AS endpoint,
              status,user_agent AS userAgent,created_at AS createdAt,updated_at AS updatedAt
            FROM mobile_push_subscription WHERE tenant_id=@TenantId AND user_name=@User
            ORDER BY updated_at DESC
            """, new { TenantId = tenantId, User = userName }, cancellationToken: cancellationToken));
        return rows.Cast<object>().ToArray();
    }

    public async Task<ProductCommandResult> RevokePushSubscriptionAsync(
        long tenantId,
        long subscriptionId,
        string userName,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var count = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE mobile_push_subscription SET status='revoked',updated_at=CURRENT_TIMESTAMP
            WHERE subscription_id=@Id AND tenant_id=@TenantId AND user_name=@User AND status='active'
            """, new { Id = subscriptionId, TenantId = tenantId, User = userName }, cancellationToken: cancellationToken));
        return count == 0
            ? new(ProductCommandStatus.NotFound, Message: "Active push subscription not found")
            : ProductCommandResult.Ok(new { subscriptionId, status = "revoked" });
    }

    public async Task<ProductCommandResult> CreateDeepLinkAsync(MobileDeepLinkRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.ObjectId, out var objectId) || objectId <= 0)
            return new(ProductCommandStatus.Invalid, Message: "objectId must be a positive numeric ID");
        var type = request.ObjectType.Trim().ToLowerInvariant();
        var (table, idColumn, tab, queryName) = type switch
        {
            "case" => ("incident_case", "case_id", "cases", "caseId"),
            "event" => ("business_event", "event_id", "events", "eventId"),
            "investigation" => ("investigation_session", "investigation_id", "investigation", "investigationId"),
            _ => (null, null, null, null)
        };
        if (table is null) return new(ProductCommandStatus.Invalid, Message: "objectType must be case, event, or investigation");
        await using var connection = connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM {table} WHERE tenant_id=@TenantId AND {idColumn}=@ObjectId)",
            new { request.TenantId, ObjectId = objectId }, cancellationToken: cancellationToken));
        if (!exists) return new(ProductCommandStatus.NotFound, Message: "Deep-link target not found");
        var path = $"/workbench/?tab={tab}&tenantId={request.TenantId}&{queryName}={objectId}";
        return ProductCommandResult.Ok(new { path, qrValue = path, requiresAuthentication = true, tenantRevalidated = true });
    }

    public async Task<ProductCommandResult> UploadCasePhotoAsync(
        long tenantId,
        long caseId,
        IFormFile file,
        double? latitude,
        double? longitude,
        string? purpose,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        var maxBytes = Math.Clamp(configuration.GetValue("CommercialProduct:Mobile:MaxPhotoBytes", 15 * 1024 * 1024), 1024, 25 * 1024 * 1024);
        if (file.Length <= 0 || file.Length > maxBytes)
            return new(ProductCommandStatus.Invalid, Message: $"Photo size must be between 1 and {maxBytes} bytes");
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180 || latitude.HasValue != longitude.HasValue)
            return new(ProductCommandStatus.Invalid, Message: "Latitude and longitude must be supplied together and be within valid ranges");
        var mediaType = file.ContentType.Trim().ToLowerInvariant();
        var extension = mediaType switch { "image/jpeg" => ".jpg", "image/png" => ".png", _ => null };
        if (extension is null) return new(ProductCommandStatus.Invalid, Message: "Only JPEG and PNG photos are accepted");

        var root = Path.GetFullPath(ProjectPaths.ResolveStorageRoot(environment));
        var folder = Path.GetFullPath(Path.Combine(root, "mobile-evidence", tenantId.ToString(), DateTimeOffset.UtcNow.ToString("yyyyMMdd")));
        if (!folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mobile evidence path escaped the storage root");
        Directory.CreateDirectory(folder);
        var name = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(folder, name + ".uploading");
        var finalPath = Path.Combine(folder, name + extension);
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await file.CopyToAsync(target, cancellationToken);
            var image = await InspectImageAsync(temporary, mediaType, cancellationToken);
            File.Move(temporary, finalPath);
            var sha256 = await HashFileAsync(finalPath, cancellationToken);
            var objectKey = "/storage/" + Path.GetRelativePath(root, finalPath).Replace('\\', '/');
            var detail = JsonSerializer.Serialize(new
            {
                image.Width, image.Height, file.FileName, file.Length,
                location = latitude.HasValue ? new { latitude, longitude, capturedWithConsent = true } : null,
                uploadedAt = DateTimeOffset.UtcNow,
                traceId
            });
            await using var connection = connectionFactory.CreateConnection();
            var evidenceId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                """
                INSERT INTO incident_case_evidence(
                  tenant_id,case_id,evidence_type,source_type,source_id,object_key,sha256,media_type,
                  masking_policy,legal_hold,purpose,added_by,detail_json)
                SELECT @TenantId,@CaseId,'photo','mobile_upload',@SourceId,@ObjectKey,@Sha256,@MediaType,
                  'original_restricted',FALSE,@Purpose,@Actor,@Detail::jsonb
                WHERE EXISTS(SELECT 1 FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId)
                ON CONFLICT(case_id,sha256) DO UPDATE SET detail_json=incident_case_evidence.detail_json || EXCLUDED.detail_json
                RETURNING evidence_id
                """, new
                {
                    TenantId = tenantId, CaseId = caseId, SourceId = name, ObjectKey = objectKey, Sha256 = sha256,
                    MediaType = mediaType, Purpose = Clean(purpose, 256) ?? "现场核查", Actor = actor, Detail = detail
                }, cancellationToken: cancellationToken));
            if (!evidenceId.HasValue)
            {
                File.Delete(finalPath);
                return new(ProductCommandStatus.NotFound, Message: "Case not found");
            }
            return ProductCommandResult.Ok(new { evidenceId, caseId, objectKey, sha256, mediaType, image.Width, image.Height, locationRecorded = latitude.HasValue });
        }
        catch (InvalidDataException ex)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            return new(ProductCommandStatus.Invalid, Message: ex.Message);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            throw;
        }
    }

    public async Task<IReadOnlyList<JsonElement>> ListDraftsAsync(long tenantId, string userName, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mobile_action_draft SET status='expired',updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND user_name=@User AND status='draft' AND expires_at<=CURRENT_TIMESTAMP",
            new { TenantId = tenantId, User = userName }, cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT to_jsonb(d)::text FROM mobile_action_draft d WHERE tenant_id=@TenantId AND user_name=@User AND status IN ('draft','conflict') ORDER BY updated_at DESC",
            new { TenantId = tenantId, User = userName }, cancellationToken: cancellationToken));
        return rows.Select(ParseJson).ToArray();
    }

    public async Task<ProductCommandResult> SyncDraftAsync(long draftId, MobileDraftSyncRequest request, string userName, string traceId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var draft = await connection.QuerySingleOrDefaultAsync<MobileDraftRow>(new CommandDefinition(
            """
            SELECT mobile_draft_id AS DraftId,tenant_id AS TenantId,user_name AS UserName,action_type AS ActionType,
              object_type AS ObjectType,object_id AS ObjectId,base_version AS BaseVersion,payload_json::text AS PayloadJson,
              status AS Status,expires_at AS ExpiresAt FROM mobile_action_draft
            WHERE mobile_draft_id=@Id AND tenant_id=@TenantId AND user_name=@User FOR UPDATE
            """, new { Id = draftId, request.TenantId, User = userName }, transaction, cancellationToken: cancellationToken));
        if (draft is null) return new(ProductCommandStatus.NotFound, Message: "Mobile draft not found");
        if (draft.Status == "synced") return new(ProductCommandStatus.Duplicate, new { mobileDraftId = draftId }, "Draft was already synced");
        if (draft.ExpiresAt <= DateTimeOffset.UtcNow) return new(ProductCommandStatus.Invalid, Message: "Mobile draft has expired");
        if (!long.TryParse(draft.ObjectId, out var objectId)) return new(ProductCommandStatus.Invalid, Message: "Draft objectId must be numeric");
        var currentVersion = draft.ObjectType switch
        {
            "case" => await connection.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT version FROM incident_case WHERE tenant_id=@TenantId AND case_id=@Id", new { draft.TenantId, Id = objectId }, transaction, cancellationToken: cancellationToken)),
            "event" => await connection.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT version FROM business_event WHERE tenant_id=@TenantId AND event_id=@Id", new { draft.TenantId, Id = objectId }, transaction, cancellationToken: cancellationToken)),
            _ => null
        };
        if (!currentVersion.HasValue) return new(ProductCommandStatus.NotFound, Message: "Draft target not found");
        var expected = request.CurrentVersion ?? draft.BaseVersion;
        if (expected.HasValue && expected != currentVersion)
        {
            var conflict = JsonSerializer.Serialize(new { expectedVersion = expected, currentVersion, objectType = draft.ObjectType, objectId });
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE mobile_action_draft SET status='conflict',conflict_json=@Conflict::jsonb,updated_at=CURRENT_TIMESTAMP WHERE mobile_draft_id=@Id",
                new { Conflict = conflict, Id = draftId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new(ProductCommandStatus.Conflict, new { mobileDraftId = draftId, currentVersion }, "Draft conflicts with a newer server version", currentVersion);
        }
        using var payloadDocument = JsonDocument.Parse(draft.PayloadJson);
        if (draft.ActionType == "case_comment" && draft.ObjectType == "case")
        {
            var content = GetString(payloadDocument.RootElement, "content");
            if (string.IsNullOrWhiteSpace(content)) return new(ProductCommandStatus.Invalid, Message: "Comment content is required");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO incident_case_comment(tenant_id,case_id,visibility,content,author_user_id,author_name)
                VALUES(@TenantId,@CaseId,'tenant',@Content,(SELECT user_id FROM sys_user WHERE user_name=@User),@User);
                UPDATE incident_case SET version=version+1,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@CaseId;
                """, new { draft.TenantId, CaseId = objectId, Content = content.Trim(), User = userName }, transaction, cancellationToken: cancellationToken));
        }
        else if (draft.ActionType == "event_acknowledge" && draft.ObjectType == "event")
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE business_event SET status='acknowledged',version=version+1,updated_at=CURRENT_TIMESTAMP
                WHERE tenant_id=@TenantId AND event_id=@EventId AND status='open';
                INSERT INTO business_event_activity(tenant_id,business_event_id,activity_type,from_status,to_status,detail_json,actor_name,trace_id,idempotency_key)
                VALUES(@TenantId,@EventId,'acknowledged','open','acknowledged',jsonb_build_object('mobileDraftId',@DraftId),@User,@TraceId,@Key)
                ON CONFLICT(tenant_id,business_event_id,idempotency_key) DO NOTHING;
                """, new { draft.TenantId, EventId = objectId, DraftId = draftId, User = userName, TraceId = traceId, Key = $"mobile-draft:{draftId}" }, transaction, cancellationToken: cancellationToken));
        }
        else return new(ProductCommandStatus.Invalid, Message: "Unsupported mobile draft action");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mobile_action_draft SET status='synced',synced_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP,conflict_json=NULL WHERE mobile_draft_id=@Id",
            new { Id = draftId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { mobileDraftId = draftId, status = "synced" });
    }

    private static string? GetString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static string? Clean(string? value, int max) { if (string.IsNullOrWhiteSpace(value)) return null; var clean = value.Trim(); return clean[..Math.Min(max, clean.Length)]; }
    private static JsonElement ParseJson(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    private static async Task<ImageInfo> InspectImageAsync(string path, string mediaType, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var header = new byte[Math.Min(stream.Length, 512 * 1024)];
        var read = await stream.ReadAsync(header, cancellationToken);
        if (mediaType == "image/png")
        {
            if (read < 24 || !header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("The uploaded file is not a valid PNG image");
            var width = ReadBigEndianInt(header, 16);
            var height = ReadBigEndianInt(header, 20);
            ValidateDimensions(width, height);
            return new(width, height);
        }
        if (read < 4 || header[0] != 0xFF || header[1] != 0xD8)
            throw new InvalidDataException("The uploaded file is not a valid JPEG image");
        var offset = 2;
        while (offset + 8 < read)
        {
            if (header[offset++] != 0xFF) continue;
            var marker = header[offset++];
            if (marker is 0xD8 or 0xD9) continue;
            if (offset + 2 > read) break;
            var length = (header[offset] << 8) | header[offset + 1];
            if (length < 2 || offset + length > read) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                var height = (header[offset + 3] << 8) | header[offset + 4];
                var width = (header[offset + 5] << 8) | header[offset + 6];
                ValidateDimensions(width, height);
                return new(width, height);
            }
            offset += length;
        }
        throw new InvalidDataException("JPEG dimensions could not be decoded");
    }

    private static int ReadBigEndianInt(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 20_000 || height > 20_000 || (long)width * height > 100_000_000)
            throw new InvalidDataException("Image dimensions exceed the allowed decode limits");
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    [GeneratedRegex("^[a-z][a-z0-9_.-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex AnalyticsNamePattern();
    private sealed record MobileDraftRow(long DraftId,long TenantId,string UserName,string ActionType,string ObjectType,string? ObjectId,int? BaseVersion,string PayloadJson,string Status,DateTimeOffset ExpiresAt);
    private sealed record ImageInfo(int Width, int Height);
}
