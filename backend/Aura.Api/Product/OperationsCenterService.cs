using Aura.Api.Data;
using Aura.Api.Ops;
using Dapper;

namespace Aura.Api.Product;

internal sealed class OperationsCenterService(
    PgSqlConnectionFactory connectionFactory,
    MediaPlatformReadinessService readiness,
    IConfiguration configuration)
{
    public async Task<object> GetAsync(long? tenantId, CancellationToken cancellationToken)
    {
        var currentReadiness = await readiness.GetAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        var tenantFilter = tenantId.HasValue ? "AND tenant_id=@TenantId" : string.Empty;
        var events = await connection.QuerySingleAsync<CountSummary>(new CommandDefinition(
            $"""
            SELECT COUNT(*) AS Total,
              COUNT(*) FILTER(WHERE status='open') AS Open,
              COUNT(*) FILTER(WHERE status='acknowledged') AS Acknowledged,
              COUNT(*) FILTER(WHERE status='dismissed') AS Dismissed
            FROM business_event WHERE TRUE {tenantFilter}
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        var cases = await connection.QuerySingleAsync<CaseOpsSummary>(new CommandDefinition(
            $"""
            SELECT COUNT(*) AS Total,
              COUNT(*) FILTER(WHERE status NOT IN ('closed','false_positive')) AS Active,
              COUNT(*) FILTER(WHERE status NOT IN ('resolved','closed','false_positive') AND resolve_due_at<CURRENT_TIMESTAMP) AS Overdue,
              COALESCE(AVG(EXTRACT(EPOCH FROM (acknowledged_at-created_at))) FILTER(WHERE acknowledged_at IS NOT NULL),0) AS MeanAcknowledgeSeconds,
              COALESCE(AVG(EXTRACT(EPOCH FROM (resolved_at-created_at))-accumulated_pause_seconds) FILTER(WHERE resolved_at IS NOT NULL),0) AS MeanResolveSeconds
            FROM incident_case WHERE TRUE {tenantFilter}
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        var notifications = await connection.QuerySingleAsync<NotificationOpsSummary>(new CommandDefinition(
            $"""
            SELECT COUNT(*) FILTER(WHERE status IN ('queued','retry_wait')) AS Pending,
              COUNT(*) FILTER(WHERE status='failed') AS Failed,
              COUNT(*) FILTER(WHERE status IN ('sent','delivered')) AS Delivered
            FROM notification_delivery WHERE TRUE {tenantFilter}
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        var tasks = await connection.QuerySingleAsync<TaskOpsSummary>(new CommandDefinition(
            $"""
            SELECT COUNT(*) FILTER(WHERE status IN ('queued','running')) AS Running,
              COUNT(*) FILTER(WHERE status='failed') AS Failed,
              COUNT(*) FILTER(WHERE status='pending_confirmation') AS AwaitingConfirmation
            FROM ops_high_risk_task WHERE TRUE {tenantFilter}
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        var snapshots = (await connection.QueryAsync<SloTrendRow>(new CommandDefinition(
            """
            SELECT s.slo_snapshot_id AS SnapshotId,p.metric_code AS MetricCode,p.tenant_id AS TenantId,
              s.window_start AS WindowStart,s.window_end AS WindowEnd,s.value AS Value,
              s.error_budget_consumed_percent AS ErrorBudgetConsumedPercent,s.status AS Status
            FROM slo_snapshot s JOIN slo_policy p ON p.slo_policy_id=s.slo_policy_id
            WHERE (@TenantId IS NULL OR p.tenant_id=@TenantId) AND s.window_end>=CURRENT_TIMESTAMP-INTERVAL '7 days'
            ORDER BY s.window_end DESC,s.slo_snapshot_id DESC LIMIT 2000
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
        var changes = (await connection.QueryAsync<ChangeRow>(new CommandDefinition(
            """
            SELECT change_id AS ChangeId,tenant_id AS TenantId,change_type AS ChangeType,
              version_ref AS VersionRef,summary AS Summary,changed_by AS ChangedBy,changed_at AS ChangedAt
            FROM platform_change_record
            WHERE (@TenantId IS NULL OR tenant_id IS NULL OR tenant_id=@TenantId)
            ORDER BY changed_at DESC,change_id DESC LIMIT 50
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
        var capacity = await GetCapacityAsync(connection, tenantId, cancellationToken);
        var entitlement = tenantId.HasValue
            ? await GetTenantCommercialStateAsync(connection, tenantId.Value, cancellationToken)
            : null;
        return new
        {
            generatedAt = DateTimeOffset.UtcNow,
            tenantId,
            readiness = currentReadiness,
            workloads = new { events, cases, notifications, highRiskTasks = tasks },
            slo = new
            {
                snapshots,
                windows = new
                {
                    fiveMinutes = Window(snapshots, TimeSpan.FromMinutes(5)),
                    oneHour = Window(snapshots, TimeSpan.FromHours(1)),
                    twentyFourHours = Window(snapshots, TimeSpan.FromHours(24)),
                    sevenDays = Window(snapshots, TimeSpan.FromDays(7))
                },
                releaseControl = ReleaseControl(snapshots)
            },
            recentChanges = changes,
            capacity,
            entitlement
        };
    }

    public async Task<ProductCommandResult> CalculateSloAsync(
        long policyId,
        long? requestedTenantId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var policy = await connection.QuerySingleOrDefaultAsync<SloPolicyRow>(new CommandDefinition(
            """
            SELECT slo_policy_id AS PolicyId,tenant_id AS TenantId,metric_code AS MetricCode,
              target AS Target,comparison AS Comparison,window_seconds AS WindowSeconds,
              warning_percent AS WarningPercent,tighten_percent AS TightenPercent,freeze_percent AS FreezePercent
            FROM slo_policy WHERE slo_policy_id=@PolicyId AND status='active'
              AND (@TenantId IS NULL OR tenant_id=@TenantId)
            """, new { PolicyId = policyId, TenantId = requestedTenantId }, cancellationToken: cancellationToken));
        if (policy is null) return new(ProductCommandStatus.NotFound, Message: "Active SLO policy not found");

        var end = DateTimeOffset.UtcNow;
        var start = end.AddSeconds(-policy.WindowSeconds);
        var measurement = await MeasureAsync(connection, policy.MetricCode, policy.TenantId, start, end, cancellationToken);
        decimal? value = measurement.Denominator > 0 ? measurement.Numerator / measurement.Denominator : null;
        decimal? consumed = value.HasValue ? ErrorBudgetConsumed(policy, value.Value) : null;
        var status = consumed switch
        {
            null => "insufficient_data",
            var x when x >= policy.FreezePercent => "frozen",
            var x when x >= policy.TightenPercent => "tightened",
            var x when x >= policy.WarningPercent => "warning",
            _ => "healthy"
        };
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO slo_snapshot(slo_policy_id,window_start,window_end,numerator,denominator,value,error_budget_consumed_percent,status,dimensions_json)
            VALUES(@PolicyId,@Start,@End,@Numerator,@Denominator,@Value,@Consumed,@Status,
              jsonb_build_object('metricCode',@MetricCode,'tenantId',@TenantId))
            ON CONFLICT(slo_policy_id,window_start,window_end) DO UPDATE SET
              numerator=EXCLUDED.numerator,denominator=EXCLUDED.denominator,value=EXCLUDED.value,
              error_budget_consumed_percent=EXCLUDED.error_budget_consumed_percent,status=EXCLUDED.status
            RETURNING slo_snapshot_id
            """, new
            {
                policy.PolicyId,
                Start = start,
                End = end,
                measurement.Numerator,
                measurement.Denominator,
                Value = value,
                Consumed = consumed,
                Status = status,
                policy.MetricCode,
                policy.TenantId
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new
        {
            snapshotId = id,
            policy.MetricCode,
            windowStart = start,
            windowEnd = end,
            measurement.Numerator,
            measurement.Denominator,
            value,
            errorBudgetConsumedPercent = consumed,
            status
        });
    }

    private async Task<object> GetCapacityAsync(System.Data.IDbConnection connection, long? tenantId, CancellationToken ct)
    {
        var databaseBytes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT pg_database_size(current_database())", cancellationToken: ct));
        var counts = await connection.QuerySingleAsync<CapacityCountRow>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(*) FROM business_event WHERE @TenantId IS NULL OR tenant_id=@TenantId) AS Events,
              (SELECT COUNT(*) FROM incident_case WHERE @TenantId IS NULL OR tenant_id=@TenantId) AS Cases,
              (SELECT COUNT(*) FROM capture_record WHERE @TenantId IS NULL OR tenant_id=@TenantId) AS Captures,
              (SELECT COUNT(*) FROM feature_embedding WHERE @TenantId IS NULL OR tenant_id=@TenantId) AS Vectors,
              (SELECT COALESCE(SUM(size_bytes),0)::bigint FROM media_artifact WHERE @TenantId IS NULL OR tenant_id=@TenantId) AS ArtifactBytes,
              (SELECT COUNT(*) FROM business_event WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND created_at>=CURRENT_TIMESTAMP-INTERVAL '24 hours') AS EventsLastDay,
              (SELECT COUNT(*) FROM capture_record WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND capture_time>=CURRENT_TIMESTAMP-INTERVAL '24 hours') AS CapturesLastDay
            """, new { TenantId = tenantId }, cancellationToken: ct));
        var capacityBytes = configuration.GetValue<long?>("CommercialProduct:Capacity:PostgresCapacityBytes");
        var remaining = capacityBytes.HasValue ? Math.Max(0, capacityBytes.Value - databaseBytes) : (long?)null;
        return new
        {
            databaseBytes,
            configuredDatabaseCapacityBytes = capacityBytes,
            remainingDatabaseBytes = remaining,
            exhaustionEstimateDays = (double?)null,
            counts,
            estimateStatus = capacityBytes.HasValue ? "capacity_configured_growth_model_pending_samples" : "capacity_not_configured"
        };
    }

    private static async Task<object> GetTenantCommercialStateAsync(System.Data.IDbConnection connection, long tenantId, CancellationToken ct)
    {
        var entitlement = await connection.QuerySingleOrDefaultAsync<EntitlementRow>(new CommandDefinition(
            """
            SELECT entitlement_code AS EntitlementCode,status AS Status,support_level AS SupportLevel,
              valid_from AS ValidFrom,valid_to AS ValidTo,grace_until AS GraceUntil,modules_json::text AS ModulesJson,limits_json::text AS LimitsJson
            FROM tenant_entitlement WHERE tenant_id=@TenantId
            ORDER BY valid_from DESC,entitlement_id DESC LIMIT 1
            """, new { TenantId = tenantId }, cancellationToken: ct));
        var quota = (await connection.QueryAsync<QuotaUsageRow>(new CommandDefinition(
            """
            SELECT q.metric_code AS MetricCode,q.limit_value AS LimitValue,q.enforcement AS Enforcement,
              q.warning_percent AS WarningPercent,COALESCE(SUM(u.quantity),0) AS Used
            FROM tenant_quota_policy q LEFT JOIN tenant_usage_ledger u
              ON u.tenant_id=q.tenant_id AND u.metric_code=q.metric_code AND u.occurred_at>=q.valid_from
              AND (q.valid_to IS NULL OR u.occurred_at<q.valid_to)
            WHERE q.tenant_id=@TenantId AND q.valid_from<=CURRENT_TIMESTAMP
              AND (q.valid_to IS NULL OR q.valid_to>CURRENT_TIMESTAMP)
            GROUP BY q.quota_policy_id,q.metric_code,q.limit_value,q.enforcement,q.warning_percent
            ORDER BY q.metric_code
            """, new { TenantId = tenantId }, cancellationToken: ct))).AsList();
        return new { entitlement, quota };
    }

    private static async Task<Measurement> MeasureAsync(
        System.Data.IDbConnection connection,
        string metricCode,
        long? tenantId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct)
    {
        var sql = metricCode switch
        {
            "case.resolve_sla_rate" => """
                SELECT COUNT(*) FILTER(WHERE resolved_at<=resolve_due_at)::numeric AS Numerator,
                  COUNT(*)::numeric AS Denominator FROM incident_case
                WHERE resolved_at>=@Start AND resolved_at<@End AND resolve_due_at IS NOT NULL
                  AND (@TenantId IS NULL OR tenant_id=@TenantId)
                """,
            "inbox.dead_letter_rate" => """
                SELECT COUNT(*) FILTER(WHERE status='dead_letter')::numeric AS Numerator,
                  COUNT(*)::numeric AS Denominator FROM media_analysis_inbox
                WHERE received_at>=@Start AND received_at<@End AND (@TenantId IS NULL OR tenant_id=@TenantId)
                """,
            "outbox.dead_letter_rate" => """
                SELECT COUNT(*) FILTER(WHERE status='dead_letter')::numeric AS Numerator,
                  COUNT(*)::numeric AS Denominator FROM integration_outbox
                WHERE created_at>=@Start AND created_at<@End AND (@TenantId IS NULL OR tenant_id=@TenantId)
                """,
            "notification.success_rate" => """
                SELECT COUNT(*) FILTER(WHERE status IN ('sent','delivered'))::numeric AS Numerator,
                  COUNT(*) FILTER(WHERE status IN ('sent','delivered','failed'))::numeric AS Denominator
                FROM notification_delivery WHERE created_at>=@Start AND created_at<@End
                  AND (@TenantId IS NULL OR tenant_id=@TenantId)
                """,
            "event.open_backlog" => """
                SELECT COUNT(*) FILTER(WHERE status='open')::numeric AS Numerator,1::numeric AS Denominator
                FROM business_event WHERE (@TenantId IS NULL OR tenant_id=@TenantId)
                """,
            _ => "SELECT 0::numeric AS Numerator,0::numeric AS Denominator"
        };
        return await connection.QuerySingleAsync<Measurement>(new CommandDefinition(
            sql, new { TenantId = tenantId, Start = start, End = end }, cancellationToken: ct));
    }

    private static decimal ErrorBudgetConsumed(SloPolicyRow policy, decimal value)
    {
        if (policy.Comparison == "lte")
            return policy.Target <= 0 ? (value <= 0 ? 0 : 1000) : Math.Max(0, value / policy.Target * 100);
        var allowedFailure = 1 - policy.Target;
        return allowedFailure <= 0 ? (value >= policy.Target ? 0 : 1000) : Math.Max(0, (1 - value) / allowedFailure * 100);
    }

    private static IReadOnlyList<SloTrendRow> Window(IReadOnlyList<SloTrendRow> rows, TimeSpan duration)
    {
        var cutoff = DateTimeOffset.UtcNow - duration;
        return rows.Where(item => item.WindowEnd >= cutoff).ToArray();
    }

    private static object ReleaseControl(IReadOnlyList<SloTrendRow> rows)
    {
        var current = rows.GroupBy(item => new { item.MetricCode, item.TenantId }).Select(group => group.First()).ToArray();
        var frozen = current.Where(item => item.Status == "frozen").ToArray();
        var tightened = current.Where(item => item.Status == "tightened").ToArray();
        return new
        {
            state = frozen.Length > 0 ? "frozen" : tightened.Length > 0 ? "tightened" : current.Any(item => item.Status == "warning") ? "warning" : "normal",
            frozenMetrics = frozen.Select(item => item.MetricCode).Distinct(),
            tightenedMetrics = tightened.Select(item => item.MetricCode).Distinct()
        };
    }

    private sealed record CountSummary(long Total,long Open,long Acknowledged,long Dismissed);
    private sealed record CaseOpsSummary(long Total,long Active,long Overdue,decimal MeanAcknowledgeSeconds,decimal MeanResolveSeconds);
    private sealed record NotificationOpsSummary(long Pending,long Failed,long Delivered);
    private sealed record TaskOpsSummary(long Running,long Failed,long AwaitingConfirmation);
    private sealed record SloPolicyRow(long PolicyId,long? TenantId,string MetricCode,decimal Target,string Comparison,long WindowSeconds,int WarningPercent,int TightenPercent,int FreezePercent);
    private sealed record Measurement(decimal Numerator,decimal Denominator);
    private sealed record CapacityCountRow(long Events,long Cases,long Captures,long Vectors,long ArtifactBytes,long EventsLastDay,long CapturesLastDay);
    private sealed record EntitlementRow(string EntitlementCode,string Status,string SupportLevel,DateTimeOffset ValidFrom,DateTimeOffset ValidTo,DateTimeOffset? GraceUntil,string ModulesJson,string LimitsJson);
    private sealed record QuotaUsageRow(string MetricCode,decimal LimitValue,string Enforcement,int WarningPercent,decimal Used);
    private sealed record ChangeRow(long ChangeId,long? TenantId,string ChangeType,string VersionRef,string Summary,string ChangedBy,DateTimeOffset ChangedAt);
    internal sealed record SloTrendRow(long SnapshotId,string MetricCode,long? TenantId,DateTimeOffset WindowStart,DateTimeOffset WindowEnd,decimal? Value,decimal? ErrorBudgetConsumedPercent,string Status);
}
