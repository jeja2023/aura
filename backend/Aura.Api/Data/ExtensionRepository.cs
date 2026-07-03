using Aura.Api.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aura.Api.Data;

internal sealed class ExtensionRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<ExtensionRepository>? _logger;

    public ExtensionRepository(PgSqlConnectionFactory connectionFactory, ILogger<ExtensionRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private static DateTime ToLocalTimestamp(DateTimeOffset value) => value.LocalDateTime;

    private static string Clean(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string JsonOrEmptyObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "{}";
        using var _ = JsonDocument.Parse(value);
        return value.Trim();
    }

    private static string JsonOrEmptyArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[]";
        using var _ = JsonDocument.Parse(value);
        return value.Trim();
    }

    public Task<List<DbAlertWorkflow>> GetAlertWorkflowsAsync(string? status, int limit = 100)
    {
        var cleanStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        limit = Math.Clamp(limit, 1, 500);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query alert workflows",
            async conn =>
            {
                var where = cleanStatus is null ? "" : "WHERE status = @Status";
                var rows = await conn.QueryAsync<DbAlertWorkflow>(
                    $"""
                    SELECT workflow_id AS WorkflowId, alert_id AS AlertId, status AS Status, assignee AS Assignee,
                           priority AS Priority, escalation_level AS EscalationLevel, handover_to AS HandoverTo,
                           note AS Note, closed_at AS ClosedAt, updated_by AS UpdatedBy,
                           updated_at AS UpdatedAt, created_at AS CreatedAt
                    FROM alert_workflow
                    {where}
                    ORDER BY updated_at DESC, workflow_id DESC
                    LIMIT @Limit
                    """,
                    new { Status = cleanStatus, Limit = limit });
                return rows.ToList();
            },
            fallback: new List<DbAlertWorkflow>(),
            logContext: new { status = cleanStatus, limit });
    }

    public Task<long?> UpsertAlertWorkflowAsync(long alertId, AlertWorkflowUpdateReq req, string? updatedBy)
    {
        var status = Clean(req.Status, "open", 32);
        var priority = Clean(req.Priority, "normal", 16);
        var escalation = Math.Clamp(req.EscalationLevel ?? 0, 0, 10);
        DateTime? closedAt = status.Equals("closed", StringComparison.OrdinalIgnoreCase) ? DateTime.Now : null;
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db upsert alert workflow",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO alert_workflow(alert_id, status, assignee, priority, escalation_level, handover_to, note, closed_at, updated_by, updated_at, created_at)
                VALUES(@AlertId, @Status, @Assignee, @Priority, @EscalationLevel, @HandoverTo, @Note, @ClosedAt, @UpdatedBy, NOW(), NOW())
                RETURNING workflow_id
                """,
                new
                {
                    AlertId = alertId,
                    Status = status,
                    Assignee = Clean(req.Assignee, "", 64),
                    Priority = priority,
                    EscalationLevel = escalation,
                    HandoverTo = Clean(req.HandoverTo, "", 64),
                    Note = req.Note,
                    ClosedAt = closedAt,
                    UpdatedBy = Clean(updatedBy, "", 64)
                }),
            fallback: null,
            logContext: new { alertId, status, priority, escalation });
    }

    public Task<List<DbSpaceTopologyEdge>> GetSpaceTopologyAsync(int limit = 1000)
    {
        limit = Math.Clamp(limit, 1, 5000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query space topology",
            async conn => (await conn.QueryAsync<DbSpaceTopologyEdge>(
                """
                SELECT edge_id AS EdgeId, from_camera_id AS FromCameraId, to_camera_id AS ToCameraId,
                       relation_type AS RelationType, weight AS Weight, created_at AS CreatedAt
                FROM space_topology_edge
                ORDER BY edge_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbSpaceTopologyEdge>(),
            logContext: new { limit });
    }

    public Task<long?> CreateSpaceTopologyEdgeAsync(SpaceTopologyEdgeReq req)
    {
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create space topology edge",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO space_topology_edge(from_camera_id, to_camera_id, relation_type, weight, created_at)
                VALUES(@FromCameraId, @ToCameraId, @RelationType, @Weight, NOW())
                RETURNING edge_id
                """,
                new
                {
                    req.FromCameraId,
                    req.ToCameraId,
                    RelationType = Clean(req.RelationType, "walkable", 32),
                    Weight = Math.Clamp(req.Weight ?? 1m, 0.0001m, 9999m)
                }),
            fallback: null,
            logContext: req);
    }

    public Task<List<DbSpaceHeatmapSnapshot>> GetSpaceHeatmapsAsync(long? floorId, int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query space heatmaps",
            async conn =>
            {
                var where = floorId.HasValue ? "WHERE floor_id = @FloorId" : "";
                var rows = await conn.QueryAsync<DbSpaceHeatmapSnapshot>(
                    $"""
                    SELECT snapshot_id AS SnapshotId, floor_id AS FloorId, bucket_start AS BucketStart,
                           bucket_minutes AS BucketMinutes, CAST(heat_json AS TEXT) AS HeatJson, created_at AS CreatedAt
                    FROM space_heatmap_snapshot
                    {where}
                    ORDER BY bucket_start DESC, snapshot_id DESC
                    LIMIT @Limit
                    """,
                    new { FloorId = floorId, Limit = limit });
                return rows.ToList();
            },
            fallback: new List<DbSpaceHeatmapSnapshot>(),
            logContext: new { floorId, limit });
    }

    public Task<long?> CreateSpaceHeatmapAsync(SpaceHeatmapSnapshotReq req)
    {
        var heatJson = JsonOrEmptyArray(req.HeatJson);
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create space heatmap",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO space_heatmap_snapshot(floor_id, bucket_start, bucket_minutes, heat_json, created_at)
                VALUES(@FloorId, @BucketStart, @BucketMinutes, CAST(@HeatJson AS jsonb), NOW())
                RETURNING snapshot_id
                """,
                new { req.FloorId, BucketStart = ToLocalTimestamp(req.BucketStart), BucketMinutes = Math.Clamp(req.BucketMinutes, 1, 1440), HeatJson = heatJson }),
            fallback: null,
            logContext: new { req.FloorId, req.BucketStart, req.BucketMinutes });
    }

    public Task<List<DbReportSchedule>> GetReportSchedulesAsync(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query report schedules",
            async conn => (await conn.QueryAsync<DbReportSchedule>(
                """
                SELECT schedule_id AS ScheduleId, report_type AS ReportType, cron_expr AS CronExpr, role_name AS RoleName,
                       delivery_channel AS DeliveryChannel, enabled AS Enabled, created_by AS CreatedBy,
                       updated_at AS UpdatedAt, created_at AS CreatedAt
                FROM report_schedule
                ORDER BY schedule_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbReportSchedule>(),
            logContext: new { limit });
    }

    public Task<long?> CreateReportScheduleAsync(ReportScheduleReq req, string? createdBy)
    {
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create report schedule",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO report_schedule(report_type, cron_expr, role_name, delivery_channel, enabled, created_by, updated_at, created_at)
                VALUES(@ReportType, @CronExpr, @RoleName, @DeliveryChannel, @Enabled, @CreatedBy, NOW(), NOW())
                RETURNING schedule_id
                """,
                new
                {
                    ReportType = Clean(req.ReportType, "daily", 32),
                    CronExpr = Clean(req.CronExpr, "0 8 * * *", 64),
                    RoleName = Clean(req.RoleName, "building_admin", 64),
                    DeliveryChannel = Clean(req.DeliveryChannel, "system", 32),
                    req.Enabled,
                    CreatedBy = Clean(createdBy, "", 64)
                }),
            fallback: null,
            logContext: new { req.ReportType, req.RoleName, req.Enabled });
    }

    public Task<List<DbReportSchedule>> GetEnabledReportSchedulesAsync()
    {
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query enabled report schedules",
            async conn => (await conn.QueryAsync<DbReportSchedule>(
                """
                SELECT schedule_id AS ScheduleId, report_type AS ReportType, cron_expr AS CronExpr, role_name AS RoleName,
                       delivery_channel AS DeliveryChannel, enabled AS Enabled, created_by AS CreatedBy,
                       updated_at AS UpdatedAt, created_at AS CreatedAt
                FROM report_schedule
                WHERE enabled = TRUE
                ORDER BY schedule_id ASC
                """)).ToList(),
            fallback: new List<DbReportSchedule>());
    }

    public Task<List<DbReportRun>> GetReportRunsAsync(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query report runs",
            async conn => (await conn.QueryAsync<DbReportRun>(
                """
                SELECT run_id AS RunId, schedule_id AS ScheduleId, report_type AS ReportType,
                       range_start AS RangeStart, range_end AS RangeEnd, status AS Status,
                       CAST(summary_json AS TEXT) AS SummaryJson, created_by AS CreatedBy, generated_at AS GeneratedAt
                FROM report_run
                ORDER BY generated_at DESC, run_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbReportRun>(),
            logContext: new { limit });
    }

    public Task<long?> CreateReportRunAsync(long? scheduleId, string reportType, DateOnly rangeStart, DateOnly rangeEnd, string summaryJson, string? createdBy)
    {
        var summary = JsonOrEmptyObject(summaryJson);
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create report run",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO report_run(schedule_id, report_type, range_start, range_end, status, summary_json, created_by, generated_at)
                VALUES(@ScheduleId, @ReportType, @RangeStart, @RangeEnd, 'generated', CAST(@SummaryJson AS jsonb), @CreatedBy, NOW())
                RETURNING run_id
                """,
                new
                {
                    ScheduleId = scheduleId,
                    ReportType = Clean(reportType, "daily", 32),
                    RangeStart = rangeStart.ToDateTime(TimeOnly.MinValue),
                    RangeEnd = rangeEnd.ToDateTime(TimeOnly.MinValue),
                    SummaryJson = summary,
                    CreatedBy = Clean(createdBy, "", 64)
                }),
            fallback: null,
            logContext: new { scheduleId, reportType, rangeStart, rangeEnd });
    }

    public Task<long?> CreateReportDeliveryAsync(long runId, string roleName, string deliveryChannel)
    {
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create report delivery",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO report_delivery(run_id, role_name, delivery_channel, status, delivered_at)
                VALUES(@RunId, @RoleName, @DeliveryChannel, 'delivered', NOW())
                RETURNING delivery_id
                """,
                new
                {
                    RunId = runId,
                    RoleName = Clean(roleName, "building_admin", 64),
                    DeliveryChannel = Clean(deliveryChannel, "system", 32)
                }),
            fallback: null,
            logContext: new { runId, roleName, deliveryChannel });
    }

    public Task<DateTime?> GetLatestReportRunAsync(long scheduleId, DateOnly rangeStart, DateOnly rangeEnd)
    {
        return PgSqlRepositoryHelpers.ExecuteAsync<DateTime?>(
            _connectionFactory,
            _logger,
            "db query latest report run",
            conn => conn.ExecuteScalarAsync<DateTime?>(
                """
                SELECT generated_at
                FROM report_run
                WHERE schedule_id = @ScheduleId AND range_start = @RangeStart AND range_end = @RangeEnd
                ORDER BY generated_at DESC
                LIMIT 1
                """,
                new
                {
                    ScheduleId = scheduleId,
                    RangeStart = rangeStart.ToDateTime(TimeOnly.MinValue),
                    RangeEnd = rangeEnd.ToDateTime(TimeOnly.MinValue)
                }),
            fallback: null,
            logContext: new { scheduleId, rangeStart, rangeEnd });
    }

    public Task<List<DbTenantProject>> GetTenantsAsync(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query tenants",
            async conn => (await conn.QueryAsync<DbTenantProject>(
                """
                SELECT tenant_id AS TenantId, tenant_code AS TenantCode, tenant_name AS TenantName,
                       CAST(config_json AS TEXT) AS ConfigJson, enabled AS Enabled, created_at AS CreatedAt
                FROM tenant_project
                ORDER BY tenant_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbTenantProject>(),
            logContext: new { limit });
    }

    public Task<long?> CreateTenantAsync(TenantProjectReq req)
    {
        var configJson = JsonOrEmptyObject(req.ConfigJson);
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create tenant",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO tenant_project(tenant_code, tenant_name, config_json, enabled, created_at)
                VALUES(@TenantCode, @TenantName, CAST(@ConfigJson AS jsonb), @Enabled, NOW())
                RETURNING tenant_id
                """,
                new
                {
                    TenantCode = Clean(req.TenantCode, "", 64),
                    TenantName = Clean(req.TenantName, "", 128),
                    ConfigJson = configJson,
                    req.Enabled
                }),
            fallback: null,
            logContext: new { req.TenantCode, req.Enabled });
    }

    public Task<List<DbTenantRoleScope>> GetTenantRoleScopesAsync(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query tenant role scopes",
            async conn => (await conn.QueryAsync<DbTenantRoleScope>(
                """
                SELECT s.scope_id AS ScopeId, s.tenant_id AS TenantId, t.tenant_code AS TenantCode,
                       t.tenant_name AS TenantName, s.role_name AS RoleName,
                       CAST(s.permission_json AS TEXT) AS PermissionJson, s.created_at AS CreatedAt
                FROM tenant_role_scope s
                JOIN tenant_project t ON t.tenant_id = s.tenant_id
                ORDER BY s.scope_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbTenantRoleScope>(),
            logContext: new { limit });
    }

    public Task<long?> UpsertTenantRoleScopeAsync(TenantRoleScopeReq req)
    {
        var permissionJson = JsonOrEmptyArray(req.PermissionJson);
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db upsert tenant role scope",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO tenant_role_scope(tenant_id, role_name, permission_json, created_at)
                VALUES(@TenantId, @RoleName, CAST(@PermissionJson AS jsonb), NOW())
                ON CONFLICT (tenant_id, role_name)
                DO UPDATE SET permission_json = EXCLUDED.permission_json
                RETURNING scope_id
                """,
                new
                {
                    req.TenantId,
                    RoleName = Clean(req.RoleName, "building_admin", 64),
                    PermissionJson = permissionJson
                }),
            fallback: null,
            logContext: new { req.TenantId, req.RoleName });
    }

    public Task<List<DbAiProviderConfig>> GetAiProvidersAsync(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query ai providers",
            async conn => (await conn.QueryAsync<DbAiProviderConfig>(
                """
                SELECT provider_id AS ProviderId, provider_name AS ProviderName, provider_type AS ProviderType,
                       endpoint_url AS EndpointUrl, model_name AS ModelName, model_version AS ModelVersion,
                       traffic_weight AS TrafficWeight, enabled AS Enabled, created_at AS CreatedAt
                FROM ai_provider_config
                ORDER BY enabled DESC, traffic_weight DESC, provider_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbAiProviderConfig>(),
            logContext: new { limit });
    }

    public Task<long?> CreateAiProviderAsync(AiProviderConfigReq req)
    {
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create ai provider",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO ai_provider_config(provider_name, provider_type, endpoint_url, model_name, model_version, traffic_weight, enabled, created_at)
                VALUES(@ProviderName, @ProviderType, @EndpointUrl, @ModelName, @ModelVersion, @TrafficWeight, @Enabled, NOW())
                RETURNING provider_id
                """,
                new
                {
                    ProviderName = Clean(req.ProviderName, "", 64),
                    ProviderType = Clean(req.ProviderType, "external", 32),
                    EndpointUrl = req.EndpointUrl.Trim(),
                    ModelName = Clean(req.ModelName, "", 128),
                    ModelVersion = Clean(req.ModelVersion, "default", 64),
                    TrafficWeight = Math.Clamp(req.TrafficWeight, 1, 100),
                    req.Enabled
                }),
            fallback: null,
            logContext: new { req.ProviderName, req.ModelName, req.Enabled });
    }

    public Task<List<DbAiAbExperiment>> GetAiExperimentsAsync(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        return PgSqlRepositoryHelpers.ExecuteAsync(
            _connectionFactory,
            _logger,
            "db query ai experiments",
            async conn => (await conn.QueryAsync<DbAiAbExperiment>(
                """
                SELECT experiment_id AS ExperimentId, experiment_name AS ExperimentName,
                       provider_a_id AS ProviderAId, provider_b_id AS ProviderBId,
                       traffic_split AS TrafficSplit, metric_name AS MetricName, enabled AS Enabled, created_at AS CreatedAt
                FROM ai_ab_experiment
                ORDER BY enabled DESC, experiment_id DESC
                LIMIT @Limit
                """,
                new { Limit = limit })).ToList(),
            fallback: new List<DbAiAbExperiment>(),
            logContext: new { limit });
    }

    public Task<long?> CreateAiExperimentAsync(AiAbExperimentReq req)
    {
        return PgSqlRepositoryHelpers.ExecuteAsync<long?>(
            _connectionFactory,
            _logger,
            "db create ai experiment",
            conn => conn.ExecuteScalarAsync<long?>(
                """
                INSERT INTO ai_ab_experiment(experiment_name, provider_a_id, provider_b_id, traffic_split, metric_name, enabled, created_at)
                VALUES(@ExperimentName, @ProviderAId, @ProviderBId, @TrafficSplit, @MetricName, @Enabled, NOW())
                RETURNING experiment_id
                """,
                new
                {
                    ExperimentName = Clean(req.ExperimentName, "", 128),
                    req.ProviderAId,
                    req.ProviderBId,
                    TrafficSplit = Math.Clamp(req.TrafficSplit, 1, 99),
                    MetricName = Clean(req.MetricName, "recall_at_k", 64),
                    req.Enabled
                }),
            fallback: null,
            logContext: new { req.ExperimentName, req.Enabled });
    }
}
