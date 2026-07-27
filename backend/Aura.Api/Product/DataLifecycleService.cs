using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class DataLifecycleService(
    PgSqlConnectionFactory connectionFactory,
    ILogger<DataLifecycleService> logger)
{
    private static readonly IReadOnlyDictionary<string, CleanupDescriptor> Descriptors =
        new Dictionary<string, CleanupDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard_event"] = new("business_event", "event_id", "last_occurred_at", "tenant_id",
                "status='dismissed' AND NOT EXISTS(SELECT 1 FROM incident_case_event ce WHERE ce.business_event_id=t.event_id AND ce.active)"),
            ["inbox"] = new("media_analysis_inbox", "inbox_id", "received_at", "tenant_id",
                "status IN ('processed','unsupported') AND NOT EXISTS(SELECT 1 FROM media_analysis_event ae JOIN business_event_source bes ON bes.analysis_event_id=ae.analysis_event_id WHERE ae.inbox_id=t.inbox_id)"),
            ["outbox"] = new("integration_outbox", "outbox_id", "created_at", "tenant_id", "status='processed'"),
            ["capture"] = new("capture_record", "capture_id", "capture_time", "tenant_id", "TRUE"),
            ["alert"] = new("alert_record", "alert_id", "created_at", "tenant_id", "TRUE"),
            ["case_activity"] = new("incident_case_activity", "activity_id", "created_at", "tenant_id", "TRUE"),
            ["audit"] = new("log_operation", "op_id", "created_at", null, "TRUE"),
            ["ai_evaluation"] = new("ai_evaluation_run", "evaluation_run_id", "created_at", "tenant_id", "status IN ('passed','failed','cancelled')")
        };

    public async Task<ProductCommandResult> CreateAsync(
        CleanupJobCreateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var batch = Math.Clamp(request.BatchLimit, 1, 10000);
        await using var connection = connectionFactory.CreateConnection();
        var policy = await connection.QuerySingleOrDefaultAsync<RetentionPolicyRow>(new CommandDefinition(
            """
            SELECT policy_id AS PolicyId,tenant_id AS TenantId,data_type AS DataType,online_days AS OnlineDays,
              archive_days AS ArchiveDays,delete_mode AS DeleteMode,status AS Status
            FROM data_retention_policy WHERE policy_id=@PolicyId
              AND (@TenantId IS NULL OR tenant_id=@TenantId)
            """, new { request.PolicyId, request.TenantId }, cancellationToken: cancellationToken));
        if (policy is null) return new(ProductCommandStatus.NotFound, Message: "Retention policy not found");
        if (policy.Status != "active") return new(ProductCommandStatus.Invalid, Message: "Only active retention policies can run cleanup");
        if (!Descriptors.ContainsKey(policy.DataType)) return new(ProductCommandStatus.Invalid, Message: "Retention data type is not executable");
        if (!request.DryRun && policy.DeleteMode.Equals("retain", StringComparison.OrdinalIgnoreCase))
            return new(ProductCommandStatus.Invalid, Message: "A retain-only policy cannot execute deletion");
        if (policy.TenantId != request.TenantId)
            return new(ProductCommandStatus.Forbidden, Message: "Policy tenant does not match the requested tenant");

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO data_cleanup_job(tenant_id,policy_id,dry_run,batch_limit,status,created_by)
            VALUES(@TenantId,@PolicyId,@DryRun,@Batch,'queued',@Actor) RETURNING cleanup_job_id
            """, new { request.TenantId, request.PolicyId, request.DryRun, Batch = batch, Actor = actor }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { cleanupJobId = id, status = "queued", request.DryRun, batchLimit = batch });
    }

    public async Task<ProductPage<CleanupJobRow>> ListAsync(
        long? tenantId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var args = new { TenantId = tenantId, Offset = (page - 1) * pageSize, PageSize = pageSize };
        const string where = "WHERE (@TenantId IS NULL OR j.tenant_id=@TenantId)";
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<CleanupJobRow>(new CommandDefinition(
            $"{JobColumns} {where} ORDER BY j.created_at DESC,j.cleanup_job_id DESC OFFSET @Offset LIMIT @PageSize",
            args, cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM data_cleanup_job j {where}", args, cancellationToken: cancellationToken));
        return new(rows, page, pageSize, total);
    }

    public async Task<CleanupJobRow?> GetAsync(long cleanupJobId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<CleanupJobRow>(new CommandDefinition(
            $"{JobColumns} WHERE j.cleanup_job_id=@Id", new { Id = cleanupJobId }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> CancelAsync(
        long cleanupJobId,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var version = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            UPDATE data_cleanup_job SET status='cancelled',cancel_requested=TRUE,
              completed_at=CURRENT_TIMESTAMP,version=version+1
            WHERE cleanup_job_id=@Id AND version=@ExpectedVersion AND status='queued'
            RETURNING version
            """, new { Id = cleanupJobId, ExpectedVersion = expectedVersion }, cancellationToken: cancellationToken));
        if (version.HasValue) return ProductCommandResult.Ok(new { cleanupJobId, status = "cancelled", version });
        var current = await GetAsync(cleanupJobId, cancellationToken);
        return current is null
            ? new(ProductCommandStatus.NotFound, Message: "Cleanup job not found")
            : new(ProductCommandStatus.Conflict, Message: "Only a queued cleanup job with the current version can be cancelled", CurrentVersion: current.Version);
    }

    internal async Task<CleanupJobRow?> ClaimAsync(CancellationToken cancellationToken)
    {
        var worker = $"{Environment.MachineName}:{Environment.ProcessId}";
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<CleanupJobRow>(new CommandDefinition(
            $"""
            WITH due AS (
              SELECT cleanup_job_id FROM data_cleanup_job
              WHERE (status='queued' OR (status='running' AND lease_until<CURRENT_TIMESTAMP))
                AND cancel_requested=FALSE
              ORDER BY cleanup_job_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE data_cleanup_job j SET status='running',worker_instance=@Worker,
              lease_until=CURRENT_TIMESTAMP+INTERVAL '10 minutes',heartbeat_at=CURRENT_TIMESTAMP,version=version+1
            FROM due WHERE j.cleanup_job_id=due.cleanup_job_id
            RETURNING j.cleanup_job_id AS CleanupJobId,j.tenant_id AS TenantId,j.policy_id AS PolicyId,
              (SELECT data_type FROM data_retention_policy WHERE policy_id=j.policy_id) AS DataType,
              j.dry_run AS DryRun,j.batch_limit AS BatchLimit,j.checkpoint AS Checkpoint,j.status AS Status,
              j.scanned_count AS ScannedCount,j.affected_count AS AffectedCount,j.skipped_hold_count AS SkippedHoldCount,
              j.failure_count AS FailureCount,j.detail_json::text AS DetailJson,j.created_by AS CreatedBy,
              j.created_at AS CreatedAt,j.completed_at AS CompletedAt,j.version AS Version
            """, new { Worker = worker }, cancellationToken: cancellationToken));
    }

    internal async Task ProcessAsync(CleanupJobRow job, CancellationToken cancellationToken)
    {
        if (!Descriptors.TryGetValue(job.DataType, out var descriptor))
            throw new InvalidOperationException($"Unsupported cleanup data type: {job.DataType}");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var policy = await connection.QuerySingleAsync<RetentionPolicyRow>(new CommandDefinition(
            """
            SELECT policy_id AS PolicyId,tenant_id AS TenantId,data_type AS DataType,online_days AS OnlineDays,
              archive_days AS ArchiveDays,delete_mode AS DeleteMode,status AS Status
            FROM data_retention_policy WHERE policy_id=@PolicyId
            """, new { job.PolicyId }, cancellationToken: cancellationToken));
        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.ArchiveDays);
        var tenantCondition = descriptor.TenantColumn is null
            ? "@TenantId IS NULL"
            : $"(@TenantId IS NULL OR t.{descriptor.TenantColumn}=@TenantId)";
        var tenantProjection = descriptor.TenantColumn is null ? "NULL::bigint" : $"t.{descriptor.TenantColumn}";
        var holdTenant = descriptor.TenantColumn is null ? "FALSE" : $"h.tenant_id=t.{descriptor.TenantColumn}";
        var evidenceBlock = job.DataType is "capture" or "alert"
            ? $"AND NOT EXISTS(SELECT 1 FROM incident_case_evidence e WHERE e.tenant_id=t.{descriptor.TenantColumn} AND e.source_type=@DataType AND e.source_id=t.{descriptor.IdColumn}::text)"
            : string.Empty;
        var baseWhere = $"{tenantCondition} AND t.{descriptor.TimeColumn}<@Cutoff AND ({descriptor.Eligibility})";
        var holdClause = descriptor.TenantColumn is null
            ? string.Empty
            : $"AND NOT EXISTS(SELECT 1 FROM legal_hold h WHERE {holdTenant} AND h.object_type=@DataType AND h.object_id=t.{descriptor.IdColumn}::text AND h.status='active')";
        var scanned = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {descriptor.Table} t WHERE {baseWhere}",
            new { job.TenantId, Cutoff = cutoff, DataType = job.DataType }, cancellationToken: cancellationToken));
        var allowed = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {descriptor.Table} t WHERE {baseWhere} {holdClause} {evidenceBlock}",
            new { job.TenantId, Cutoff = cutoff, DataType = job.DataType }, cancellationToken: cancellationToken));
        var candidates = (await connection.QueryAsync<CleanupCandidate>(new CommandDefinition(
            $"""
            SELECT t.{descriptor.IdColumn} AS ObjectId,{tenantProjection} AS TenantId,to_jsonb(t)::text AS SnapshotJson
            FROM {descriptor.Table} t WHERE {baseWhere} {holdClause} {evidenceBlock}
              AND (@Checkpoint IS NULL OR t.{descriptor.IdColumn}>@Checkpoint)
            ORDER BY t.{descriptor.IdColumn} LIMIT @Batch
            """, new
            {
                job.TenantId,
                Cutoff = cutoff,
                DataType = job.DataType,
                Checkpoint = long.TryParse(job.Checkpoint, out var checkpoint) ? checkpoint : (long?)null,
                Batch = job.BatchLimit
            }, cancellationToken: cancellationToken))).AsList();
        var skipped = Math.Max(0, scanned - allowed);

        if (job.DryRun)
        {
            await CompleteJobAsync(connection, job.CleanupJobId, scanned, allowed, skipped, 0,
                new { cutoff, mode = "dry_run", sampleObjectIds = candidates.Take(100).Select(item => item.ObjectId), deleteMode = policy.DeleteMode }, cancellationToken);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var deleted = 0L;
        foreach (var candidate in candidates)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.SnapshotJson))).ToLowerInvariant();
            var archiveId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO data_archive_record(tenant_id,data_type,object_id,policy_id,snapshot_json,snapshot_sha256)
                VALUES(@TenantId,@DataType,@ObjectId,@PolicyId,@Snapshot::jsonb,@Hash)
                ON CONFLICT(tenant_id,data_type,object_id,policy_id) DO UPDATE SET snapshot_json=EXCLUDED.snapshot_json
                RETURNING archive_record_id
                """, new { candidate.TenantId, DataType = job.DataType, ObjectId = candidate.ObjectId.ToString(), job.PolicyId, Snapshot = candidate.SnapshotJson, Hash = hash }, transaction, cancellationToken: cancellationToken));
            await DeleteDependenciesAsync(connection, transaction, job.DataType, candidate.ObjectId, cancellationToken);
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {descriptor.Table} WHERE {descriptor.IdColumn}=@ObjectId",
                new { candidate.ObjectId }, transaction, cancellationToken: cancellationToken));
            if (affected == 0) continue;
            deleted += affected;
            var outboxId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO integration_outbox(tenant_id,aggregate_type,aggregate_id,event_type,payload_json)
                VALUES(@TenantId,'data_lifecycle',@ObjectId,'data.deleted',
                  jsonb_build_object('dataType',@DataType,'objectId',@ObjectId,'archiveRecordId',@ArchiveId,'derivedIndexes',jsonb_build_array('vector','graph','object_storage')))
                RETURNING outbox_id
                """, new { candidate.TenantId, ObjectId = candidate.ObjectId.ToString(), DataType = job.DataType, ArchiveId = archiveId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO data_deletion_tombstone(tenant_id,cleanup_job_id,data_type,object_id,archive_record_id,deletion_event_id,deleted_by)
                VALUES(@TenantId,@JobId,@DataType,@ObjectId,@ArchiveId,@OutboxId,@Actor)
                ON CONFLICT(cleanup_job_id,data_type,object_id) DO NOTHING
                """, new { candidate.TenantId, JobId = job.CleanupJobId, DataType = job.DataType, ObjectId = candidate.ObjectId.ToString(), ArchiveId = archiveId, OutboxId = outboxId, Actor = job.CreatedBy }, transaction, cancellationToken: cancellationToken));
        }
        var lastId = candidates.Count > 0 ? candidates[^1].ObjectId : (long?)null;
        var hasMore = lastId.HasValue && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 FROM {descriptor.Table} t WHERE {baseWhere} {holdClause} {evidenceBlock} AND t.{descriptor.IdColumn}>@LastId)",
            new { job.TenantId, Cutoff = cutoff, DataType = job.DataType, LastId = lastId }, transaction, cancellationToken: cancellationToken));
        if (hasMore)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE data_cleanup_job SET status='queued',checkpoint=@Checkpoint,
                  scanned_count=scanned_count+@BatchScanned,affected_count=affected_count+@Affected,
                  skipped_hold_count=GREATEST(skipped_hold_count,@Skipped),detail_json=@Detail::jsonb,
                  worker_instance=NULL,lease_until=NULL,heartbeat_at=CURRENT_TIMESTAMP,version=version+1
                WHERE cleanup_job_id=@JobId
                """, new
                {
                    JobId = job.CleanupJobId,
                    Checkpoint = lastId!.Value.ToString(),
                    BatchScanned = candidates.Count,
                    Affected = deleted,
                    Skipped = skipped,
                    Detail = JsonSerializer.Serialize(new { cutoff, mode = "delete", status = "continuing", lastObjectId = lastId, deleteMode = policy.DeleteMode })
                }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await CompleteJobAsync(connection, job.CleanupJobId, job.ScannedCount + candidates.Count,
                job.AffectedCount + deleted, Math.Max(job.SkippedHoldCount, skipped), job.FailureCount,
                new { cutoff, mode = "delete", archived = job.AffectedCount + deleted, derivedDeletionEvents = job.AffectedCount + deleted, deleteMode = policy.DeleteMode }, cancellationToken, transaction);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task FailAsync(CleanupJobRow job, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Data cleanup job failed. jobId={JobId}, dataType={DataType}", job.CleanupJobId, job.DataType);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE data_cleanup_job SET status='failed',failure_count=failure_count+1,
              detail_json=jsonb_build_object('errorType',@ErrorType,'error',@Error),
              lease_until=NULL,completed_at=CURRENT_TIMESTAMP,version=version+1
            WHERE cleanup_job_id=@Id
            """, new { Id = job.CleanupJobId, ErrorType = exception.GetType().Name, Error = exception.Message }, cancellationToken: cancellationToken));
    }

    private static async Task DeleteDependenciesAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        string dataType,
        long objectId,
        CancellationToken ct)
    {
        var statements = dataType switch
        {
            "standard_event" => new[]
            {
                "DELETE FROM business_event_activity WHERE business_event_id=@Id",
                "DELETE FROM business_event_source WHERE business_event_id=@Id"
            },
            "alert" => new[] { "DELETE FROM alert_workflow WHERE alert_id=@Id" },
            "ai_evaluation" => new[] { "DELETE FROM ai_evaluation_item WHERE evaluation_run_id=@Id" },
            _ => Array.Empty<string>()
        };
        foreach (var sql in statements)
            await connection.ExecuteAsync(new CommandDefinition(sql, new { Id = objectId }, transaction, cancellationToken: ct));
    }

    private static async Task CompleteJobAsync(
        System.Data.IDbConnection connection,
        long jobId,
        long scanned,
        long affected,
        long skipped,
        long failures,
        object detail,
        CancellationToken ct,
        System.Data.IDbTransaction? transaction = null)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE data_cleanup_job SET status='completed',scanned_count=@Scanned,affected_count=@Affected,
              skipped_hold_count=@Skipped,failure_count=@Failures,detail_json=@Detail::jsonb,
              checkpoint=NULL,lease_until=NULL,heartbeat_at=CURRENT_TIMESTAMP,completed_at=CURRENT_TIMESTAMP,version=version+1
            WHERE cleanup_job_id=@JobId
            """, new { JobId = jobId, Scanned = scanned, Affected = affected, Skipped = skipped, Failures = failures, Detail = JsonSerializer.Serialize(detail) }, transaction, cancellationToken: ct));
    }

    private const string JobColumns = """
        SELECT j.cleanup_job_id AS CleanupJobId,j.tenant_id AS TenantId,j.policy_id AS PolicyId,
          p.data_type AS DataType,j.dry_run AS DryRun,j.batch_limit AS BatchLimit,j.checkpoint AS Checkpoint,
          j.status AS Status,j.scanned_count AS ScannedCount,j.affected_count AS AffectedCount,
          j.skipped_hold_count AS SkippedHoldCount,j.failure_count AS FailureCount,j.detail_json::text AS DetailJson,
          j.created_by AS CreatedBy,j.created_at AS CreatedAt,j.completed_at AS CompletedAt,j.version AS Version
        FROM data_cleanup_job j JOIN data_retention_policy p ON p.policy_id=j.policy_id
        """;

    private sealed record CleanupDescriptor(string Table,string IdColumn,string TimeColumn,string? TenantColumn,string Eligibility);
    private sealed record RetentionPolicyRow(long PolicyId,long? TenantId,string DataType,int OnlineDays,int ArchiveDays,string DeleteMode,string Status);
    private sealed record CleanupCandidate(long ObjectId,long? TenantId,string SnapshotJson);
}

internal sealed record CleanupJobRow(
    long CleanupJobId,long? TenantId,long PolicyId,string DataType,bool DryRun,int BatchLimit,string? Checkpoint,
    string Status,long ScannedCount,long AffectedCount,long SkippedHoldCount,long FailureCount,string DetailJson,
    string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? CompletedAt,int Version);

internal sealed class DataCleanupWorker(
    DataLifecycleService lifecycle,
    IConfiguration configuration,
    ILogger<DataCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CommercialProduct:DataCleanupWorker:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            CleanupJobRow? job = null;
            try
            {
                job = await lifecycle.ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                await lifecycle.ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Data cleanup worker iteration failed");
                if (job is not null) await lifecycle.FailAsync(job, ex, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
