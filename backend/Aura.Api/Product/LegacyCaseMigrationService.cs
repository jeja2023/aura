using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class LegacyCaseMigrationService(PgSqlConnectionFactory connectionFactory)
{
    public async Task<object> PreflightAsync(long? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var summary = await connection.QuerySingleAsync<PreflightSummary>(new CommandDefinition(
            """
            SELECT COUNT(*) AS SourceAlerts,
              COUNT(*) FILTER(WHERE a.tenant_id IS NULL) AS MissingTenant,
              COUNT(*) FILTER(WHERE a.tenant_id IS NOT NULL AND t.tenant_id IS NULL) AS InvalidTenant,
              COUNT(*) FILTER(WHERE w.assignee IS NOT NULL AND u.user_id IS NULL) AS MissingAssignee,
              COUNT(DISTINCT a.tenant_id) FILTER(WHERE a.tenant_id IS NOT NULL) AS TenantCount
            FROM alert_record a
            LEFT JOIN tenant_project t ON t.tenant_id=a.tenant_id
            LEFT JOIN LATERAL (
              SELECT assignee FROM alert_workflow WHERE alert_id=a.alert_id ORDER BY updated_at DESC,workflow_id DESC LIMIT 1) w ON TRUE
            LEFT JOIN sys_user u ON u.user_name=w.assignee AND u.status=1
            WHERE (@TenantId IS NULL OR a.tenant_id=@TenantId)
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        var byTenant = (await connection.QueryAsync<TenantPreflightRow>(new CommandDefinition(
            """
            SELECT a.tenant_id AS TenantId,COUNT(*) AS AlertCount,
              COUNT(*) FILTER(WHERE EXISTS(SELECT 1 FROM alert_workflow w WHERE w.alert_id=a.alert_id)) AS WorkflowCount,
              MIN(a.created_at) AS EarliestAt,MAX(a.created_at) AS LatestAt
            FROM alert_record a WHERE (@TenantId IS NULL OR a.tenant_id=@TenantId)
            GROUP BY a.tenant_id ORDER BY a.tenant_id NULLS FIRST
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
        return new
        {
            tenantId,
            summary,
            byTenant,
            blockingIssues = new
            {
                missingTenant = summary.MissingTenant,
                invalidTenant = summary.InvalidTenant
            },
            quarantineIssues = new { missingAssignee = summary.MissingAssignee },
            canStart = summary.MissingTenant == 0 && summary.InvalidTenant == 0
        };
    }

    public async Task<ProductCommandResult> StartAsync(
        LegacyMigrationStartRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var batch = CleanBatch(request.BatchName);
        if (request.ShadowDeadline <= DateTimeOffset.UtcNow)
            return new(ProductCommandStatus.Invalid, Message: "Shadow deadline must be in the future");
        var preflight = await PreflightAsync(request.TenantId, cancellationToken);
        var json = JsonSerializer.Serialize(preflight);
        using var document = JsonDocument.Parse(json);
        var canStart = document.RootElement.GetProperty("canStart").GetBoolean();
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO legacy_migration_run(migration_batch,tenant_id,status,read_mode,dry_run,preflight_json,shadow_owner,shadow_deadline,created_by)
            VALUES(@Batch,@TenantId,@Status,'legacy',@DryRun,@Preflight::jsonb,@Actor,@Deadline,@Actor)
            ON CONFLICT(migration_batch) DO NOTHING RETURNING migration_run_id
            """, new
            {
                Batch = batch,
                request.TenantId,
                Status = canStart ? "ready" : "blocked",
                request.DryRun,
                Preflight = json,
                Actor = actor,
                Deadline = request.ShadowDeadline
            }, cancellationToken: cancellationToken));
        if (!id.HasValue)
            return new(ProductCommandStatus.Conflict, Message: "Migration batch name already exists");
        return ProductCommandResult.Ok(new { migrationRunId = id.Value, batch, status = canStart ? "ready" : "blocked", preflight });
    }

    public async Task<LegacyMigrationRunRow?> GetAsync(long runId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<LegacyMigrationRunRow>(new CommandDefinition(
            $"{RunColumns} WHERE migration_run_id=@RunId", new { RunId = runId }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> BackfillAsync(
        long runId,
        LegacyMigrationBackfillRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(request.BatchSize, 1, 5000);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<LegacyMigrationRunRow>(new CommandDefinition(
            $"{RunColumns} WHERE migration_run_id=@RunId", new { RunId = runId }, cancellationToken: cancellationToken));
        if (run is null) return new(ProductCommandStatus.NotFound, Message: "Migration run not found");
        if (run.Status is "blocked" or "cutover" or "completed" or "rolled_back")
            return new(ProductCommandStatus.Invalid, Message: $"Migration cannot backfill in state {run.Status}");
        var alerts = (await connection.QueryAsync<LegacyAlertRow>(new CommandDefinition(
            """
            SELECT a.alert_id AS AlertId,a.tenant_id AS TenantId,a.alert_type AS AlertType,a.vid AS Vid,
              a.room_id AS RoomId,a.detail_json::text AS DetailJson,a.created_at AS CreatedAt,
              w.workflow_id AS WorkflowId,w.status AS WorkflowStatus,w.assignee AS Assignee,
              w.priority AS Priority,w.note AS Note,w.closed_at AS ClosedAt,w.updated_by AS UpdatedBy,w.updated_at AS UpdatedAt,
              u.user_id AS AssigneeUserId
            FROM alert_record a
            LEFT JOIN LATERAL (SELECT * FROM alert_workflow aw WHERE aw.alert_id=a.alert_id ORDER BY aw.updated_at DESC,aw.workflow_id DESC LIMIT 1) w ON TRUE
            LEFT JOIN sys_user u ON u.user_name=w.assignee AND u.status=1
            WHERE (@TenantId IS NULL OR a.tenant_id=@TenantId) AND a.alert_id>@Checkpoint
            ORDER BY a.alert_id LIMIT @BatchSize
            """, new { run.TenantId, Checkpoint = run.CheckpointAlertId ?? 0, BatchSize = batchSize }, cancellationToken: cancellationToken))).AsList();
        if (run.DryRun)
        {
            var quarantined = alerts.Count(item => !item.TenantId.HasValue || (item.WorkflowId.HasValue && !string.IsNullOrWhiteSpace(item.Assignee) && !item.AssigneeUserId.HasValue));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE legacy_migration_run SET status='ready',reconciliation_json=jsonb_build_object(
                  'mode','dry_run','sampleCount',@Count,'wouldQuarantine',@Quarantined,'lastAlertId',@Last),updated_at=CURRENT_TIMESTAMP,version=version+1
                WHERE migration_run_id=@RunId
                """, new { RunId = runId, Count = alerts.Count, Quarantined = quarantined, Last = alerts.LastOrDefault()?.AlertId }, cancellationToken: cancellationToken));
            return ProductCommandResult.Ok(new { runId, mode = "dry_run", sampleCount = alerts.Count, wouldQuarantine = quarantined });
        }

        long migrated = 0, quarantinedCount = 0, failed = 0;
        foreach (var alert in alerts)
        {
            try
            {
                var outcome = await MigrateOneAsync(connection, run, alert, actor, cancellationToken);
                migrated += outcome.Migrated;
                quarantinedCount += outcome.Quarantined;
            }
            catch (Exception ex)
            {
                failed++;
                await WriteMapAsync(connection, "alert_record", alert.AlertId, alert.TenantId, null, null,
                    run.MigrationBatch, "failed", ex.Message, Checksum(alert), cancellationToken);
            }
        }
        var last = alerts.LastOrDefault()?.AlertId ?? run.CheckpointAlertId;
        var remaining = last.HasValue && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM alert_record WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND alert_id>@Last)",
            new { run.TenantId, Last = last }, cancellationToken: cancellationToken));
        var status = remaining ? "backfilling" : "shadow";
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE legacy_migration_run SET status=@Status,read_mode=CASE WHEN @Status='shadow' THEN 'shadow' ELSE read_mode END,
              checkpoint_alert_id=@Last,migrated_count=migrated_count+@Migrated,
              quarantined_count=quarantined_count+@Quarantined,failed_count=failed_count+@Failed,
              updated_at=CURRENT_TIMESTAMP,version=version+1 WHERE migration_run_id=@RunId
            """, new { RunId = runId, Status = status, Last = last, Migrated = migrated, Quarantined = quarantinedCount, Failed = failed }, cancellationToken: cancellationToken));
        var reconciliation = await ReconcileAsync(runId, cancellationToken);
        return ProductCommandResult.Ok(new { runId, status, batchProcessed = alerts.Count, migrated, quarantined = quarantinedCount, failed, remaining, reconciliation });
    }

    public async Task<object?> ReconcileAsync(long runId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var run = await connection.QuerySingleOrDefaultAsync<LegacyMigrationRunRow>(new CommandDefinition(
            $"{RunColumns} WHERE migration_run_id=@RunId", new { RunId = runId }, cancellationToken: cancellationToken));
        if (run is null) return null;
        var result = await connection.QuerySingleAsync<ReconciliationRow>(new CommandDefinition(
            """
            SELECT
              (SELECT COUNT(*) FROM alert_record WHERE @TenantId IS NULL OR tenant_id=@TenantId) AS SourceAlerts,
              (SELECT COUNT(*) FROM alert_workflow w JOIN alert_record a ON a.alert_id=w.alert_id WHERE @TenantId IS NULL OR a.tenant_id=@TenantId) AS SourceWorkflows,
              (SELECT COUNT(*) FROM legacy_case_migration_map WHERE migration_batch=@Batch AND source_table='alert_record' AND status='migrated') AS MigratedAlerts,
              (SELECT COUNT(*) FROM legacy_case_migration_map WHERE migration_batch=@Batch AND source_table='alert_workflow' AND status='migrated') AS MigratedWorkflows,
              (SELECT COUNT(*) FROM legacy_case_migration_map WHERE migration_batch=@Batch AND status='quarantined') AS Quarantined,
              (SELECT COUNT(*) FROM legacy_case_migration_map WHERE migration_batch=@Batch AND status='failed') AS Failed,
              (SELECT COUNT(DISTINCT target_id) FROM legacy_case_migration_map WHERE migration_batch=@Batch AND target_type='business_event' AND status='migrated') AS TargetEvents,
              (SELECT COUNT(DISTINCT target_id) FROM legacy_case_migration_map WHERE migration_batch=@Batch AND target_type='incident_case' AND status='migrated') AS TargetCases
            """, new { run.TenantId, Batch = run.MigrationBatch }, cancellationToken: cancellationToken));
        var complete = result.SourceAlerts == result.MigratedAlerts + result.Quarantined + result.Failed
            && result.MigratedAlerts == result.TargetEvents
            && result.MigratedWorkflows == result.TargetCases;
        var report = new { runId, run.MigrationBatch, result, complete, reconciledAt = DateTimeOffset.UtcNow };
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE legacy_migration_run SET reconciliation_json=@Report::jsonb,updated_at=CURRENT_TIMESTAMP WHERE migration_run_id=@RunId",
            new { RunId = runId, Report = JsonSerializer.Serialize(report) }, cancellationToken: cancellationToken));
        return report;
    }

    public async Task<ProductCommandResult> CutoverAsync(
        long runId,
        LegacyMigrationCutoverRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var mode = request.TargetReadMode.Trim().ToLowerInvariant();
        if (mode is not ("shadow" or "authoritative" or "legacy"))
            return new(ProductCommandStatus.Invalid, Message: "Read mode must be legacy, shadow, or authoritative");
        if (string.IsNullOrWhiteSpace(request.ApprovalReference))
            return new(ProductCommandStatus.Invalid, Message: "Cutover approval reference is required");
        var reconciliation = await ReconcileAsync(runId, cancellationToken);
        if (reconciliation is null) return new(ProductCommandStatus.NotFound, Message: "Migration run not found");
        var complete = JsonSerializer.SerializeToElement(reconciliation).GetProperty("complete").GetBoolean();
        if (mode == "authoritative" && !complete)
            return new(ProductCommandStatus.Invalid, Message: "Authoritative cutover is blocked until reconciliation completes");
        await using var connection = connectionFactory.CreateConnection();
        var version = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            UPDATE legacy_migration_run SET read_mode=@Mode,
              status=CASE WHEN @Mode='authoritative' THEN 'cutover' WHEN @Mode='shadow' THEN 'shadow' ELSE 'rolled_back' END,
              cutover_approved_by=@Actor,cutover_at=CASE WHEN @Mode='authoritative' THEN CURRENT_TIMESTAMP ELSE cutover_at END,
              reconciliation_json=reconciliation_json||jsonb_build_object('approvalReference',@Approval),
              updated_at=CURRENT_TIMESTAMP,version=version+1
            WHERE migration_run_id=@RunId AND version=@ExpectedVersion
              AND (@Mode<>'authoritative' OR shadow_deadline>CURRENT_TIMESTAMP)
            RETURNING version
            """, new { RunId = runId, Mode = mode, Actor = actor, Approval = request.ApprovalReference.Trim(), request.ExpectedVersion }, cancellationToken: cancellationToken));
        return version.HasValue
            ? ProductCommandResult.Ok(new { runId, readMode = mode, version })
            : new(ProductCommandStatus.Conflict, Message: "Migration version conflict or shadow deadline expired");
    }

    private static async Task<MigrationOutcome> MigrateOneAsync(
        Npgsql.NpgsqlConnection connection,
        LegacyMigrationRunRow run,
        LegacyAlertRow alert,
        string actor,
        CancellationToken ct)
    {
        if (!alert.TenantId.HasValue)
        {
            await WriteMapAsync(connection, "alert_record", alert.AlertId, null, null, null, run.MigrationBatch,
                "quarantined", "missing_tenant", Checksum(alert), ct);
            return new(0, 1);
        }
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var tenantId = alert.TenantId.Value;
        var eventId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO business_event(tenant_id,event_no,event_type,title,summary,severity,status,entity_ref,space_ref,
              aggregation_key,aggregation_policy_version,first_occurred_at,last_occurred_at,representative_evidence_json,created_at,updated_at)
            VALUES(@TenantId,@EventNo,@EventType,@Title,@Summary,@Severity,@Status,@EntityRef,@SpaceRef,
              @Aggregation,1,@Occurred,@Occurred,@Evidence::jsonb,@Occurred,@Occurred)
            ON CONFLICT(tenant_id,aggregation_key,aggregation_policy_version) DO UPDATE SET updated_at=business_event.updated_at
            RETURNING event_id
            """, new
            {
                TenantId = tenantId,
                EventNo = $"MIG-E-{tenantId}-{alert.AlertId}",
                EventType = $"legacy.{CleanCode(alert.AlertType)}",
                Title = $"Legacy alert {alert.AlertId}: {alert.AlertType}",
                Summary = alert.DetailJson,
                Severity = MapSeverity(alert.Priority),
                Status = alert.WorkflowId.HasValue ? "linked" : "open",
                EntityRef = alert.Vid,
                SpaceRef = alert.RoomId?.ToString(),
                Aggregation = $"legacy:alert:{alert.AlertId}",
                Occurred = alert.CreatedAt,
                Evidence = string.IsNullOrWhiteSpace(alert.DetailJson) ? "{}" : alert.DetailJson
            }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO business_event_activity(tenant_id,business_event_id,activity_type,to_status,detail_json,actor_name,idempotency_key,created_at)
            VALUES(@TenantId,@EventId,'legacy_migrated',@Status,jsonb_build_object('sourceTable','alert_record','sourceId',@AlertId),@Actor,@Key,@Occurred)
            ON CONFLICT(tenant_id,business_event_id,idempotency_key) DO NOTHING
            """, new { TenantId = tenantId, EventId = eventId, Status = alert.WorkflowId.HasValue ? "linked" : "open", alert.AlertId, Actor = actor, Key = $"migration:{run.MigrationBatch}:alert:{alert.AlertId}", Occurred = alert.CreatedAt }, transaction, cancellationToken: ct));
        await WriteMapAsync(connection, "alert_record", alert.AlertId, tenantId, "business_event", eventId,
            run.MigrationBatch, "migrated", null, Checksum(alert), ct, transaction);

        if (!alert.WorkflowId.HasValue)
        {
            await transaction.CommitAsync(ct);
            return new(1, 0);
        }
        if (!string.IsNullOrWhiteSpace(alert.Assignee) && !alert.AssigneeUserId.HasValue)
        {
            await WriteMapAsync(connection, "alert_workflow", alert.WorkflowId.Value, tenantId, null, null,
                run.MigrationBatch, "quarantined", $"missing_identity:{alert.Assignee}", Checksum(alert), ct, transaction);
            await transaction.CommitAsync(ct);
            return new(1, 1);
        }

        var caseStatus = MapCaseStatus(alert.WorkflowStatus);
        var caseId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO incident_case(tenant_id,case_no,title,description,status,status_reason,priority,owner_user_id,owner_name,
              resolution_json,acknowledged_at,started_at,resolved_at,closed_at,created_at,updated_at)
            VALUES(@TenantId,@CaseNo,@Title,@Description,@Status,'legacy_migration',@Priority,@OwnerId,@OwnerName,
              CASE WHEN @Status IN ('resolved','closed','false_positive') THEN jsonb_build_object('source','legacy_workflow','note',@Note) ELSE NULL END,
              CASE WHEN @Status IN ('acknowledged','in_progress','paused','escalated','resolved','closed') THEN @Updated ELSE NULL END,
              CASE WHEN @Status IN ('in_progress','paused','escalated','resolved','closed') THEN @Updated ELSE NULL END,
              CASE WHEN @Status IN ('resolved','closed') THEN COALESCE(@Closed,@Updated) ELSE NULL END,
              CASE WHEN @Status='closed' THEN COALESCE(@Closed,@Updated) ELSE NULL END,@Created,@Updated)
            ON CONFLICT(tenant_id,case_no) DO UPDATE SET updated_at=incident_case.updated_at
            RETURNING case_id
            """, new
            {
                TenantId = tenantId,
                CaseNo = $"MIG-C-{tenantId}-{alert.WorkflowId.Value}",
                Title = $"Legacy workflow {alert.WorkflowId.Value}: {alert.AlertType}",
                Description = alert.Note ?? alert.DetailJson,
                Status = caseStatus,
                Priority = MapPriority(alert.Priority),
                OwnerId = alert.AssigneeUserId,
                OwnerName = alert.Assignee,
                Note = alert.Note,
                Updated = alert.UpdatedAt ?? alert.CreatedAt,
                Closed = alert.ClosedAt,
                Created = alert.CreatedAt
            }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_event(tenant_id,case_id,event_id,relation_type,relation_reason,linked_by,linked_at)
            VALUES(@TenantId,@CaseId,@EventId,'primary','legacy_migration',@Actor,@Occurred)
            ON CONFLICT(case_id,event_id,relation_type) DO NOTHING;
            INSERT INTO incident_case_activity(tenant_id,case_id,activity_type,to_status,reason_code,detail_json,actor_name,idempotency_key,created_at)
            VALUES(@TenantId,@CaseId,'legacy_migrated',@Status,'legacy_migration',jsonb_build_object('sourceTable','alert_workflow','sourceId',@WorkflowId),@Actor,@Key,@Updated)
            ON CONFLICT(tenant_id,case_id,idempotency_key) DO NOTHING;
            """, new { TenantId = tenantId, CaseId = caseId, EventId = eventId, Status = caseStatus, alert.WorkflowId, Actor = actor, Key = $"migration:{run.MigrationBatch}:workflow:{alert.WorkflowId}", Occurred = alert.CreatedAt, Updated = alert.UpdatedAt ?? alert.CreatedAt }, transaction, cancellationToken: ct));
        await WriteMapAsync(connection, "alert_workflow", alert.WorkflowId.Value, tenantId, "incident_case", caseId,
            run.MigrationBatch, "migrated", null, Checksum(alert), ct, transaction);
        await transaction.CommitAsync(ct);
        return new(2, 0);
    }

    private static Task WriteMapAsync(
        System.Data.IDbConnection connection,string sourceTable,long sourceId,long? tenantId,string? targetType,long? targetId,
        string batch,string status,string? reason,string checksum,CancellationToken ct,System.Data.IDbTransaction? tx=null) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO legacy_case_migration_map(source_table,source_id,tenant_id,target_type,target_id,migration_batch,status,reason,checksum)
            VALUES(@SourceTable,@SourceId,@TenantId,@TargetType,@TargetId,@Batch,@Status,@Reason,@Checksum)
            ON CONFLICT(source_table,source_id,migration_batch) DO UPDATE SET target_type=EXCLUDED.target_type,target_id=EXCLUDED.target_id,
              status=EXCLUDED.status,reason=EXCLUDED.reason,checksum=EXCLUDED.checksum
            """, new { SourceTable = sourceTable, SourceId = sourceId, TenantId = tenantId, TargetType = targetType, TargetId = targetId, Batch = batch, Status = status, Reason = reason, Checksum = checksum }, tx, cancellationToken: ct));

    private static string CleanBatch(string value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is 0 or > 128 || text.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("Batch name must use letters, numbers, dot, dash, or underscore and be at most 128 characters");
        return text;
    }
    private static string CleanCode(string? value) => new((value ?? "unknown").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
    private static string MapSeverity(string? value) => value?.ToLowerInvariant() switch { "urgent" => "critical", "high" => "high", "low" => "low", _ => "medium" };
    private static string MapPriority(string? value) => value?.ToLowerInvariant() switch { "urgent" => "urgent", "high" => "high", "low" => "low", _ => "normal" };
    private static string MapCaseStatus(string? value) => value?.ToLowerInvariant() switch
    {
        "assigned" or "acknowledged" => "acknowledged",
        "processing" or "in_progress" => "in_progress",
        "paused" => "paused",
        "escalated" => "escalated",
        "resolved" => "resolved",
        "false_positive" => "false_positive",
        "closed" => "closed",
        _ => "new"
    };
    private static string Checksum(LegacyAlertRow row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row)))).ToLowerInvariant();

    private const string RunColumns = """
        SELECT migration_run_id AS MigrationRunId,migration_batch AS MigrationBatch,tenant_id AS TenantId,
          status AS Status,read_mode AS ReadMode,dry_run AS DryRun,preflight_json::text AS PreflightJson,
          reconciliation_json::text AS ReconciliationJson,checkpoint_alert_id AS CheckpointAlertId,
          migrated_count AS MigratedCount,quarantined_count AS QuarantinedCount,failed_count AS FailedCount,
          shadow_owner AS ShadowOwner,shadow_deadline AS ShadowDeadline,cutover_approved_by AS CutoverApprovedBy,
          cutover_at AS CutoverAt,created_by AS CreatedBy,created_at AS CreatedAt,updated_at AS UpdatedAt,version AS Version
        FROM legacy_migration_run
        """;

    private sealed record PreflightSummary(long SourceAlerts,long MissingTenant,long InvalidTenant,long MissingAssignee,long TenantCount);
    private sealed record TenantPreflightRow(long? TenantId,long AlertCount,long WorkflowCount,DateTime? EarliestAt,DateTime? LatestAt);
    private sealed record ReconciliationRow(long SourceAlerts,long SourceWorkflows,long MigratedAlerts,long MigratedWorkflows,long Quarantined,long Failed,long TargetEvents,long TargetCases);
    private sealed record LegacyAlertRow(long AlertId,long? TenantId,string AlertType,string? Vid,long? RoomId,string? DetailJson,DateTimeOffset CreatedAt,long? WorkflowId,string? WorkflowStatus,string? Assignee,string? Priority,string? Note,DateTimeOffset? ClosedAt,string? UpdatedBy,DateTimeOffset? UpdatedAt,long? AssigneeUserId);
    private sealed record MigrationOutcome(long Migrated,long Quarantined);
}

internal sealed record LegacyMigrationRunRow(
    long MigrationRunId,string MigrationBatch,long? TenantId,string Status,string ReadMode,bool DryRun,
    string PreflightJson,string ReconciliationJson,long? CheckpointAlertId,long MigratedCount,long QuarantinedCount,long FailedCount,
    string ShadowOwner,DateTimeOffset ShadowDeadline,string? CutoverApprovedBy,DateTimeOffset? CutoverAt,
    string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,int Version);
