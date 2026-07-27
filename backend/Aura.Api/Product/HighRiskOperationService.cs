using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Api.Data;
using Aura.Api.Graph;
using Aura.Api.Vector;
using Dapper;

namespace Aura.Api.Product;

internal sealed class StepUpAuthorizationService(IConfiguration configuration)
{
    public bool HasRecentStepUp(ClaimsPrincipal user)
    {
        var amr = user.FindAll("amr").Select(claim => claim.Value);
        var acr = user.FindFirstValue("acr") ?? string.Empty;
        if (amr.Any(value => value.Contains("mfa", StringComparison.OrdinalIgnoreCase))
            || acr.Contains("mfa", StringComparison.OrdinalIgnoreCase)
            || acr.Contains("high", StringComparison.OrdinalIgnoreCase))
            return true;
        return user.IsInRole("super_admin") && configuration.GetValue("Security:StepUp:AllowLocalSuperAdmin", false);
    }
}

internal sealed class HighRiskOperationService(
    PgSqlConnectionFactory connectionFactory,
    StepUpAuthorizationService stepUp)
{
    private static readonly HashSet<string> SupportedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "inbox_replay", "artifact_replay", "outbox_replay", "graph_rebuild", "vector_backfill",
        "subscription_bulk_disable", "provider_bulk_disable", "rule_bulk_disable"
    };

    public async Task<ProductCommandResult> PreviewAsync(
        HighRiskTaskPreviewRequest request,
        string actor,
        string traceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var operation = request.OperationType.Trim().ToLowerInvariant();
        if (!SupportedOperations.Contains(operation))
            return new(ProductCommandStatus.Invalid, Message: "不支持的高风险操作类型");
        var maxBatch = operation == "graph_rebuild" ? 1 : 1000;
        var batch = Math.Clamp(request.RequestedBatchSize, 1, maxBatch);
        var scopeNode = request.Scope.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(request.Scope.GetRawText())!.AsObject()
            : new JsonObject();
        scopeNode["batchSize"] = batch;
        var scopeJson = scopeNode.ToJsonString();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var affected = await CountAffectedAsync(connection, request.TenantId, operation, scopeJson, batch, cancellationToken);
        var estimatedSeconds = operation switch
        {
            "graph_rebuild" => Math.Max(60, affected / 500),
            "vector_backfill" => Math.Max(30, affected / 100),
            _ => Math.Max(1, affected / 100)
        };
        var phrase = $"CONFIRM {operation.ToUpperInvariant()} {affected}";
        var impact = JsonSerializer.Serialize(new
        {
            request.TenantId,
            affectedCount = affected,
            requestedBatchSize = request.RequestedBatchSize,
            effectiveBatchSize = batch,
            estimatedSeconds,
            potentialSideEffects = operation.Contains("replay", StringComparison.Ordinal)
                ? new[] { "下游任务会重新执行", "幂等边界之外的外部系统需单独核验" }
                : new[] { "操作会占用后台处理容量", "执行期间状态可能短暂降级" }
        });
        var requestHash = Hash($"{operation}|{request.TenantId}|{scopeJson}|{batch}");
        var taskId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO ops_high_risk_task(
              tenant_id,operation_type,scope_json,impact_json,request_hash,idempotency_key,status,
              confirmation_phrase,ticket_no,requested_by,trace_id)
            VALUES(@TenantId,@Operation,@Scope::jsonb,@Impact::jsonb,@Hash,@Key,'pending_confirmation',
              @Phrase,@TicketNo,@Actor,@TraceId)
            ON CONFLICT(requested_by,operation_type,idempotency_key) DO NOTHING
            RETURNING task_id
            """, new
            {
                request.TenantId,
                Operation = operation,
                Scope = scopeJson,
                Impact = impact,
                Hash = requestHash,
                Key = idempotencyKey,
                Phrase = phrase,
                TicketNo = CleanNullable(request.TicketNo, 128),
                Actor = actor,
                TraceId = traceId
            }, cancellationToken: cancellationToken));
        if (!taskId.HasValue)
        {
            var prior = await connection.QuerySingleAsync<HighRiskTaskRow>(new CommandDefinition(
                $"{TaskColumns} WHERE requested_by=@Actor AND operation_type=@Operation AND idempotency_key=@Key",
                new { Actor = actor, Operation = operation, Key = idempotencyKey }, cancellationToken: cancellationToken));
            if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
                return new(ProductCommandStatus.Conflict, Message: "同一幂等键已用于不同的预览请求");
            return new(ProductCommandStatus.Duplicate, prior, "影响预览已存在");
        }
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ops_high_risk_task_activity(task_id,activity_type,detail_json,actor,trace_id)
            VALUES(@TaskId,'impact_previewed',@Impact::jsonb,@Actor,@TraceId)
            """, new { TaskId = taskId.Value, Impact = impact, Actor = actor, TraceId = traceId }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new
        {
            taskId,
            status = "pending_confirmation",
            confirmationPhrase = phrase,
            impact = JsonSerializer.Deserialize<JsonElement>(impact)
        });
    }

    public async Task<ProductCommandResult> ExecuteAsync(
        long taskId,
        HighRiskTaskExecuteRequest request,
        ClaimsPrincipal user,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (!stepUp.HasRecentStepUp(user))
            return new(ProductCommandStatus.Forbidden, Message: "该操作需要近期 MFA 或 step-up 认证");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<HighRiskTaskRow>(new CommandDefinition(
            $"{TaskColumns} WHERE task_id=@TaskId FOR UPDATE", new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
        if (current is null) return new(ProductCommandStatus.NotFound, Message: "高风险任务不存在");
        if (current.Version != request.ExpectedVersion)
            return new(ProductCommandStatus.Conflict, Message: "任务已被其他用户更新", CurrentVersion: current.Version);
        if (current.Status is "queued" or "running" or "succeeded")
            return new(ProductCommandStatus.Duplicate, current, "任务已提交");
        if (!string.Equals(current.ConfirmationPhrase, request.ConfirmationPhrase?.Trim(), StringComparison.Ordinal))
            return new(ProductCommandStatus.Invalid, Message: "确认短语不匹配");
        var ticket = CleanNullable(request.TicketNo, 128) ?? current.TicketNo;
        if (string.IsNullOrWhiteSpace(ticket))
            return new(ProductCommandStatus.Invalid, Message: "高影响操作必须填写工单号");
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE ops_high_risk_task SET status='queued',ticket_no=@Ticket,step_up_verified=TRUE,
              version=version+1,result_json='{}'::jsonb
            WHERE task_id=@TaskId RETURNING version
            """, new { TaskId = taskId, Ticket = ticket }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ops_high_risk_task_activity(task_id,activity_type,detail_json,actor,trace_id)
            VALUES(@TaskId,'confirmed_and_queued',jsonb_build_object('ticketNo',@Ticket),@Actor,@TraceId)
            """, new { TaskId = taskId, Ticket = ticket, Actor = actor, TraceId = traceId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { taskId, status = "queued", version });
    }

    public async Task<ProductPage<HighRiskTaskRow>> ListAsync(long? tenantId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var args = new { TenantId = tenantId, Status = CleanNullable(status, 24), Offset = (page - 1) * pageSize, PageSize = pageSize };
        const string where = "WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND (@Status IS NULL OR status=@Status)";
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<HighRiskTaskRow>(new CommandDefinition(
            $"{TaskColumns} {where} ORDER BY created_at DESC,task_id DESC OFFSET @Offset LIMIT @PageSize",
            args, cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM ops_high_risk_task {where}", args, cancellationToken: cancellationToken));
        return new(rows, page, pageSize, total);
    }

    public async Task<HighRiskTaskRow?> GetAsync(long taskId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<HighRiskTaskRow>(new CommandDefinition(
            $"{TaskColumns} WHERE task_id=@TaskId", new { TaskId = taskId }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> CancelAsync(
        long taskId,
        int expectedVersion,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<HighRiskTaskRow>(new CommandDefinition(
            $"{TaskColumns} WHERE task_id=@TaskId FOR UPDATE", new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
        if (current is null) return new(ProductCommandStatus.NotFound, Message: "High-risk task not found");
        if (current.Version != expectedVersion)
            return new(ProductCommandStatus.Conflict, Message: "Task version conflict", CurrentVersion: current.Version);
        if (current.Status is "succeeded" or "failed" or "cancelled")
            return new(ProductCommandStatus.Conflict, Message: "Completed tasks cannot be cancelled", CurrentVersion: current.Version);
        if (current.Status == "running")
            return new(ProductCommandStatus.Invalid, Message: "The task has entered its critical section and can no longer be cancelled");
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE ops_high_risk_task SET status='cancelled',cancel_requested=TRUE,
              completed_at=CURRENT_TIMESTAMP,version=version+1
            WHERE task_id=@TaskId RETURNING version
            """, new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO ops_high_risk_task_activity(task_id,activity_type,detail_json,actor,trace_id) VALUES(@TaskId,'cancelled','{}'::jsonb,@Actor,@TraceId)",
            new { TaskId = taskId, Actor = actor, TraceId = traceId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { taskId, status = "cancelled", version });
    }

    internal async Task<HighRiskTaskRow?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var worker = $"{Environment.MachineName}:{Environment.ProcessId}";
        return await connection.QuerySingleOrDefaultAsync<HighRiskTaskRow>(new CommandDefinition(
            """
            WITH due AS (
              SELECT task_id FROM ops_high_risk_task
              WHERE (status='queued' OR (status='running' AND lease_until<CURRENT_TIMESTAMP))
                AND cancel_requested=FALSE
              ORDER BY task_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE ops_high_risk_task task SET status='running',started_at=COALESCE(started_at,CURRENT_TIMESTAMP),
              progress=GREATEST(progress,1),version=version+1,worker_instance=@Worker,
              lease_until=CURRENT_TIMESTAMP+INTERVAL '15 minutes'
            FROM due WHERE task.task_id=due.task_id
            RETURNING task.task_id AS TaskId,task.tenant_id AS TenantId,task.operation_type AS OperationType,
              task.scope_json::text AS ScopeJson,task.impact_json::text AS ImpactJson,task.request_hash AS RequestHash,
              task.idempotency_key AS IdempotencyKey,task.status AS Status,task.progress AS Progress,
              task.confirmation_phrase AS ConfirmationPhrase,task.ticket_no AS TicketNo,task.step_up_verified AS StepUpVerified,
              task.result_json::text AS ResultJson,task.requested_by AS RequestedBy,task.trace_id AS TraceId,
              task.started_at AS StartedAt,task.completed_at AS CompletedAt,task.created_at AS CreatedAt,task.version AS Version
            """, new { Worker = worker }, cancellationToken: cancellationToken));
    }

    internal async Task<bool> IsCancellationRequestedAsync(long taskId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT cancel_requested FROM ops_high_risk_task WHERE task_id=@TaskId",
            new { TaskId = taskId }, cancellationToken: cancellationToken));
    }

    internal async Task MarkCancelledAsync(long taskId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH changed AS (
              UPDATE ops_high_risk_task SET status='cancelled',completed_at=CURRENT_TIMESTAMP,
                lease_until=NULL,progress=100,version=version+1
              WHERE task_id=@TaskId AND status='running' RETURNING task_id)
            INSERT INTO ops_high_risk_task_activity(task_id,activity_type,detail_json,actor)
            SELECT task_id,'cancelled','{}'::jsonb,@Actor FROM changed
            """, new { TaskId = taskId, Actor = actor }, cancellationToken: cancellationToken));
    }

    internal async Task CompleteAsync(long taskId, bool success, object result, string actor, CancellationToken cancellationToken)
    {
        var resultJson = JsonSerializer.Serialize(result);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ops_high_risk_task SET status=@Status,progress=100,result_json=@Result::jsonb,
              completed_at=CURRENT_TIMESTAMP,lease_until=NULL,version=version+1 WHERE task_id=@TaskId
            """, new { TaskId = taskId, Status = success ? "succeeded" : "failed", Result = resultJson }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO ops_high_risk_task_activity(task_id,activity_type,detail_json,actor) VALUES(@TaskId,@Activity,@Result::jsonb,@Actor)",
            new { TaskId = taskId, Activity = success ? "completed" : "failed", Result = resultJson, Actor = actor }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<object> ExecuteDatabaseOperationAsync(HighRiskTaskRow task, CancellationToken cancellationToken)
    {
        using var scope = JsonDocument.Parse(task.ScopeJson);
        var batch = Batch(scope.RootElement);
        await using var connection = connectionFactory.CreateConnection();
        var affected = task.OperationType switch
        {
            "inbox_replay" => await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH selected AS (SELECT inbox_id FROM media_analysis_inbox
                  WHERE status IN ('dead_letter','unsupported','retry_wait') AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  ORDER BY inbox_id LIMIT @Batch FOR UPDATE SKIP LOCKED)
                UPDATE media_analysis_inbox i SET status='received',next_attempt_at=NOW(),locked_by=NULL,lock_until=NULL,
                  processed_at=NULL,last_error_code=NULL,last_error=NULL FROM selected WHERE i.inbox_id=selected.inbox_id
                """, new { task.TenantId, Batch = batch }, cancellationToken: cancellationToken)),
            "artifact_replay" => await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH selected AS (SELECT a.artifact_id FROM media_artifact a JOIN media_analysis_event e ON e.analysis_event_id=a.analysis_event_id
                  WHERE a.archive_status IN ('dead_letter','failed','retry_wait') AND (@TenantId IS NULL OR e.tenant_id=@TenantId)
                  ORDER BY a.artifact_id LIMIT @Batch FOR UPDATE OF a SKIP LOCKED)
                UPDATE media_artifact a SET archive_status='pending',next_attempt_at=NOW(),locked_by=NULL,lock_until=NULL,last_error=NULL
                FROM selected WHERE a.artifact_id=selected.artifact_id
                """, new { task.TenantId, Batch = batch }, cancellationToken: cancellationToken)),
            "outbox_replay" => await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH selected AS (SELECT outbox_id FROM integration_outbox
                  WHERE status IN ('dead_letter','retry_wait') AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  ORDER BY outbox_id LIMIT @Batch FOR UPDATE SKIP LOCKED)
                UPDATE integration_outbox o SET status='pending',available_at=NOW(),locked_by=NULL,lock_until=NULL,last_error=NULL
                FROM selected WHERE o.outbox_id=selected.outbox_id
                """, new { task.TenantId, Batch = batch }, cancellationToken: cancellationToken)),
            "subscription_bulk_disable" => await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH selected AS (SELECT subscription_id FROM media_analysis_subscription
                  WHERE desired_state<>'stopped' AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  ORDER BY subscription_id LIMIT @Batch FOR UPDATE SKIP LOCKED)
                UPDATE media_analysis_subscription s SET desired_state='stopped',updated_at=NOW()
                FROM selected WHERE s.subscription_id=selected.subscription_id
                """, new { task.TenantId, Batch = batch }, cancellationToken: cancellationToken)),
            "provider_bulk_disable" => await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH selected AS (SELECT provider_id FROM media_analysis_provider
                  WHERE enabled=TRUE AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  ORDER BY provider_id LIMIT @Batch FOR UPDATE SKIP LOCKED)
                UPDATE media_analysis_provider p SET enabled=FALSE,updated_at=NOW()
                FROM selected WHERE p.provider_id=selected.provider_id
                """, new { task.TenantId, Batch = batch }, cancellationToken: cancellationToken)),
            "rule_bulk_disable" => await connection.ExecuteAsync(new CommandDefinition(
                """
                WITH selected AS (SELECT rule_id FROM automation_rule
                  WHERE status IN ('canary','published') AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  ORDER BY rule_id LIMIT @Batch FOR UPDATE SKIP LOCKED)
                UPDATE automation_rule r SET status='paused',updated_at=NOW()
                FROM selected WHERE r.rule_id=selected.rule_id
                """, new { task.TenantId, Batch = batch }, cancellationToken: cancellationToken)),
            _ => 0
        };
        return new { affected, batch, task.OperationType, task.TenantId };
    }

    private static async Task<long> CountAffectedAsync(
        System.Data.IDbConnection connection,
        long? tenantId,
        string operation,
        string scopeJson,
        int batch,
        CancellationToken cancellationToken)
    {
        var sql = operation switch
        {
            "inbox_replay" => "SELECT COUNT(*) FROM media_analysis_inbox WHERE status IN ('dead_letter','unsupported','retry_wait') AND (@TenantId IS NULL OR tenant_id=@TenantId)",
            "artifact_replay" => "SELECT COUNT(*) FROM media_artifact a JOIN media_analysis_event e ON e.analysis_event_id=a.analysis_event_id WHERE a.archive_status IN ('dead_letter','failed','retry_wait') AND (@TenantId IS NULL OR e.tenant_id=@TenantId)",
            "outbox_replay" => "SELECT COUNT(*) FROM integration_outbox WHERE status IN ('dead_letter','retry_wait') AND (@TenantId IS NULL OR tenant_id=@TenantId)",
            "subscription_bulk_disable" => "SELECT COUNT(*) FROM media_analysis_subscription WHERE desired_state<>'stopped' AND (@TenantId IS NULL OR tenant_id=@TenantId)",
            "provider_bulk_disable" => "SELECT COUNT(*) FROM media_analysis_provider WHERE enabled=TRUE AND (@TenantId IS NULL OR tenant_id=@TenantId)",
            "rule_bulk_disable" => "SELECT COUNT(*) FROM automation_rule WHERE status IN ('canary','published') AND (@TenantId IS NULL OR tenant_id=@TenantId)",
            "graph_rebuild" => "SELECT COUNT(*) FROM integration_outbox",
            "vector_backfill" => "SELECT COUNT(*) FROM feature_embedding WHERE (@TenantId IS NULL OR tenant_id=@TenantId)",
            _ => "SELECT 0"
        };
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { TenantId = tenantId, Scope = scopeJson }, cancellationToken: cancellationToken));
        return Math.Min(count, batch);
    }

    private static int Batch(JsonElement scope) => scope.TryGetProperty("batchSize", out var value) && value.TryGetInt32(out var batch)
        ? Math.Clamp(batch, 1, 1000)
        : 500;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? CleanNullable(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private const string TaskColumns = """
        SELECT task_id AS TaskId,tenant_id AS TenantId,operation_type AS OperationType,scope_json::text AS ScopeJson,
          impact_json::text AS ImpactJson,request_hash AS RequestHash,idempotency_key AS IdempotencyKey,status AS Status,
          progress AS Progress,confirmation_phrase AS ConfirmationPhrase,ticket_no AS TicketNo,step_up_verified AS StepUpVerified,
          result_json::text AS ResultJson,requested_by AS RequestedBy,trace_id AS TraceId,started_at AS StartedAt,
          completed_at AS CompletedAt,created_at AS CreatedAt,version AS Version FROM ops_high_risk_task
        """;
}

internal sealed record HighRiskTaskRow(
    long TaskId,long? TenantId,string OperationType,string ScopeJson,string ImpactJson,string RequestHash,
    string IdempotencyKey,string Status,int Progress,string? ConfirmationPhrase,string? TicketNo,bool StepUpVerified,
    string ResultJson,string RequestedBy,string? TraceId,DateTimeOffset? StartedAt,DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,int Version);

internal sealed class HighRiskTaskWorker(
    HighRiskOperationService operations,
    GraphProjectionRepository graphProjection,
    VectorMigrationService vectorMigration,
    IConfiguration configuration,
    ILogger<HighRiskTaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CommercialProduct:HighRiskWorker:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            HighRiskTaskRow? activeTask = null;
            try
            {
                activeTask = await operations.ClaimAsync(stoppingToken);
                if (activeTask is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                var task = activeTask;
                object result;
                if (task.OperationType == "graph_rebuild")
                {
                    var rebuildId = await graphProjection.CreateRebuildAsync(task.RequestedBy, stoppingToken);
                    result = new { rebuildId, delegated = true };
                }
                else if (task.OperationType == "vector_backfill")
                {
                    using var scope = JsonDocument.Parse(task.ScopeJson);
                    var root = scope.RootElement;
                    var request = new VectorBackfillRequest(
                        root.TryGetProperty("migrationName", out var name) ? name.GetString() ?? "commercial-backfill" : "commercial-backfill",
                        task.TenantId ?? throw new InvalidOperationException("向量回填必须指定租户"),
                        root.TryGetProperty("modelId", out var model) ? model.GetInt64() : 1,
                        root.TryGetProperty("batchSize", out var batch) ? batch.GetInt32() : 500,
                        root.TryGetProperty("maxBatches", out var max) ? max.GetInt32() : 1,
                        root.TryGetProperty("restart", out var restart) && restart.GetBoolean());
                    result = await vectorMigration.BackfillAsync(request, stoppingToken);
                }
                else
                {
                    result = await operations.ExecuteDatabaseOperationAsync(task, stoppingToken);
                }
                await operations.CompleteAsync(task.TaskId, true, result, "high-risk-worker", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "高风险后台任务执行失败。");
                if (activeTask is not null)
                {
                    await operations.CompleteAsync(activeTask.TaskId, false, new
                    {
                        errorType = ex.GetType().Name,
                        error = ex.Message,
                        operation = activeTask.OperationType
                    }, "high-risk-worker", CancellationToken.None);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
