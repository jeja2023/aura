using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class EventCaseRepository(
    PgSqlConnectionFactory connectionFactory,
    ILogger<EventCaseRepository> logger)
{
    public async Task<ProductPage<DbBusinessEvent>> GetEventsAsync(
        long tenantId,
        string? status,
        string? severity,
        string? keyword,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;
        const string where = """
            WHERE e.tenant_id=@TenantId
              AND (@Status IS NULL OR e.status=@Status)
              AND (@Severity IS NULL OR e.severity=@Severity)
              AND (@Keyword IS NULL OR e.title ILIKE '%' || @Keyword || '%' OR e.summary ILIKE '%' || @Keyword || '%' OR e.event_no ILIKE '%' || @Keyword || '%')
              AND (@From IS NULL OR e.last_occurred_at>=@From)
              AND (@To IS NULL OR e.last_occurred_at<=@To)
            """;
        await using var connection = connectionFactory.CreateConnection();
        var args = new
        {
            TenantId = tenantId,
            Status = CleanNullable(status, 24),
            Severity = CleanNullable(severity, 16),
            Keyword = CleanNullable(keyword, 128),
            From = from,
            To = to,
            Offset = offset,
            PageSize = pageSize
        };
        var rows = (await connection.QueryAsync<DbBusinessEvent>(new CommandDefinition(
            $"""
            SELECT e.event_id AS EventId,e.tenant_id AS TenantId,e.event_no AS EventNo,e.event_type AS EventType,
                   e.title AS Title,e.summary AS Summary,e.severity AS Severity,e.status AS Status,
                   e.triage_user_id AS TriageUserId,e.triage_user_name AS TriageUserName,
                   e.rule_code AS RuleCode,e.rule_version AS RuleVersion,e.model_code AS ModelCode,e.model_version AS ModelVersion,
                   e.entity_ref AS EntityRef,e.space_ref AS SpaceRef,e.occurrence_count AS OccurrenceCount,
                   e.first_occurred_at AS FirstOccurredAt,e.last_occurred_at AS LastOccurredAt,
                   e.representative_evidence_json::text AS RepresentativeEvidenceJson,e.version AS Version,
                   e.created_at AS CreatedAt,e.updated_at AS UpdatedAt,
                   linked.case_id AS CaseId,linked.case_no AS CaseNo,linked.status AS CaseStatus
            FROM business_event e
            LEFT JOIN LATERAL (
              SELECT c.case_id,c.case_no,c.status
              FROM incident_case_event ce JOIN incident_case c ON c.case_id=ce.case_id AND c.tenant_id=ce.tenant_id
              WHERE ce.tenant_id=e.tenant_id AND ce.event_id=e.event_id AND ce.active=TRUE
              ORDER BY ce.linked_at DESC LIMIT 1
            ) linked ON TRUE
            {where}
            ORDER BY e.last_occurred_at DESC,e.event_id DESC
            OFFSET @Offset LIMIT @PageSize
            """, args, cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM business_event e {where}", args, cancellationToken: cancellationToken));
        return new(rows, page, pageSize, total);
    }

    public async Task<DbBusinessEvent?> GetEventAsync(long tenantId, long eventId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<DbBusinessEvent>(new CommandDefinition(
            """
            SELECT e.event_id AS EventId,e.tenant_id AS TenantId,e.event_no AS EventNo,e.event_type AS EventType,
                   e.title AS Title,e.summary AS Summary,e.severity AS Severity,e.status AS Status,
                   e.triage_user_id AS TriageUserId,e.triage_user_name AS TriageUserName,
                   e.rule_code AS RuleCode,e.rule_version AS RuleVersion,e.model_code AS ModelCode,e.model_version AS ModelVersion,
                   e.entity_ref AS EntityRef,e.space_ref AS SpaceRef,e.occurrence_count AS OccurrenceCount,
                   e.first_occurred_at AS FirstOccurredAt,e.last_occurred_at AS LastOccurredAt,
                   e.representative_evidence_json::text AS RepresentativeEvidenceJson,e.version AS Version,
                   e.created_at AS CreatedAt,e.updated_at AS UpdatedAt,
                   linked.case_id AS CaseId,linked.case_no AS CaseNo,linked.status AS CaseStatus
            FROM business_event e
            LEFT JOIN LATERAL (
              SELECT c.case_id,c.case_no,c.status
              FROM incident_case_event ce JOIN incident_case c ON c.case_id=ce.case_id AND c.tenant_id=ce.tenant_id
              WHERE ce.tenant_id=e.tenant_id AND ce.event_id=e.event_id AND ce.active=TRUE
              ORDER BY ce.linked_at DESC LIMIT 1
            ) linked ON TRUE
            WHERE e.tenant_id=@TenantId AND e.event_id=@EventId
            """, new { TenantId = tenantId, EventId = eventId }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> CreateOrAggregateEventAsync(
        BusinessEventCreateRequest request,
        string actor,
        string traceId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var eventNo = $"EVT-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        var evidenceJson = request.RepresentativeEvidence?.GetRawText() ?? "{}";
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var row = await connection.QuerySingleAsync<EventUpsertRow>(new CommandDefinition(
                """
                INSERT INTO business_event(
                  tenant_id,event_no,event_type,title,summary,severity,status,rule_code,rule_version,
                  model_code,model_version,entity_ref,space_ref,aggregation_key,aggregation_policy_version,
                  occurrence_count,first_occurred_at,last_occurred_at,representative_evidence_json)
                VALUES(
                  @TenantId,@EventNo,@EventType,@Title,@Summary,@Severity,'open',@RuleCode,@RuleVersion,
                  @ModelCode,@ModelVersion,@EntityRef,@SpaceRef,@AggregationKey,@AggregationPolicyVersion,
                  1,@OccurredAt,@OccurredAt,@EvidenceJson::jsonb)
                ON CONFLICT(tenant_id,aggregation_key,aggregation_policy_version) DO UPDATE SET
                  occurrence_count=business_event.occurrence_count+1,
                  last_occurred_at=GREATEST(business_event.last_occurred_at,EXCLUDED.last_occurred_at),
                  representative_evidence_json=EXCLUDED.representative_evidence_json,
                  updated_at=CURRENT_TIMESTAMP,
                  version=business_event.version+1
                RETURNING event_id AS EventId,event_no AS EventNo,status AS Status,version AS Version,(xmax=0) AS Inserted
                """, new
                {
                    request.TenantId,
                    EventNo = eventNo,
                    EventType = Clean(request.EventType, "event", 128),
                    Title = Clean(request.Title, "未命名事件", 256),
                    Summary = CleanNullable(request.Summary, 4000),
                    Severity = NormalizeSeverity(request.Severity),
                    request.RuleCode,
                    request.RuleVersion,
                    request.ModelCode,
                    request.ModelVersion,
                    EntityRef = CleanNullable(request.EntityRef, 256),
                    SpaceRef = CleanNullable(request.SpaceRef, 256),
                    AggregationKey = Clean(request.AggregationKey, Guid.NewGuid().ToString("N"), 512),
                    AggregationPolicyVersion = Math.Max(1, request.AggregationPolicyVersion),
                    request.OccurredAt,
                    EvidenceJson = evidenceJson
                }, transaction, cancellationToken: cancellationToken));

            if (request.AnalysisEventId.HasValue)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO business_event_source(tenant_id,business_event_id,analysis_event_id)
                    SELECT @TenantId,@EventId,analysis_event_id FROM media_analysis_event
                    WHERE tenant_id=@TenantId AND analysis_event_id=@AnalysisEventId
                    ON CONFLICT DO NOTHING
                    """, new { request.TenantId, row.EventId, request.AnalysisEventId }, transaction, cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO business_event_activity(
                  tenant_id,business_event_id,activity_type,to_status,detail_json,actor_user_id,actor_name,trace_id,idempotency_key)
                VALUES(
                  @TenantId,@EventId,@ActivityType,@Status,jsonb_build_object('occurrenceAt',@OccurredAt),
                  (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId,@IdempotencyKey)
                ON CONFLICT(tenant_id,business_event_id,idempotency_key) DO NOTHING
                """, new
                {
                    request.TenantId,
                    row.EventId,
                    ActivityType = row.Inserted ? "created" : "occurrence_aggregated",
                    row.Status,
                    request.OccurredAt,
                    Actor = actor,
                    TraceId = traceId,
                    IdempotencyKey = CleanNullable(idempotencyKey, 128)
                }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return ProductCommandResult.Ok(new { row.EventId, row.EventNo, row.Version, aggregated = !row.Inserted });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(ex, "创建或聚合业务事件失败。tenantId={TenantId}", request.TenantId);
            throw;
        }
    }

    public async Task<ProductCommandResult> TransitionEventAsync(
        long tenantId,
        long eventId,
        EventTransitionRequest request,
        string actor,
        string traceId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<EventStateRow>(new CommandDefinition(
            "SELECT status AS Status,version AS Version FROM business_event WHERE tenant_id=@TenantId AND event_id=@EventId FOR UPDATE",
            new { TenantId = tenantId, EventId = eventId }, transaction, cancellationToken: cancellationToken));
        if (current is null) return new(ProductCommandStatus.NotFound, Message: "事件不存在");
        if (current.Version != request.ExpectedVersion)
            return new(ProductCommandStatus.Conflict, Message: "事件已被其他用户更新", CurrentVersion: current.Version);
        if (!BusinessEventStateMachine.TryResolve(current.Status, request.Action, request.ReasonCode, out var target, out var error))
            return new(ProductCommandStatus.Invalid, Message: error);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM business_event_activity WHERE tenant_id=@TenantId AND business_event_id=@EventId AND idempotency_key=@Key)",
                new { TenantId = tenantId, EventId = eventId, Key = idempotencyKey }, transaction, cancellationToken: cancellationToken));
            if (exists) return new(ProductCommandStatus.Duplicate, new { eventId, version = current.Version, status = current.Status }, "请求已处理");
        }

        var nextVersion = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE business_event SET status=@Target,version=version+1,updated_at=CURRENT_TIMESTAMP
            WHERE tenant_id=@TenantId AND event_id=@EventId AND version=@ExpectedVersion
            RETURNING version
            """, new { TenantId = tenantId, EventId = eventId, Target = target, request.ExpectedVersion }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO business_event_activity(
              tenant_id,business_event_id,activity_type,from_status,to_status,reason_code,detail_json,
              actor_user_id,actor_name,trace_id,idempotency_key)
            VALUES(
              @TenantId,@EventId,@Action,@FromStatus,@Target,@Reason,@Detail::jsonb,
              (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId,@Key)
            """, new
            {
                TenantId = tenantId,
                EventId = eventId,
                Action = Clean(request.Action, "transition", 64),
                FromStatus = current.Status,
                Target = target,
                Reason = CleanNullable(request.ReasonCode, 64),
                Detail = request.Detail?.GetRawText() ?? "{}",
                Actor = actor,
                TraceId = traceId,
                Key = CleanNullable(idempotencyKey, 128)
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { eventId, status = target, version = nextVersion });
    }

    public async Task<IReadOnlyList<object>> BatchAssignEventsAsync(
        EventBatchTriageRequest request,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        var ids = request.EventIds.Distinct().Take(200).ToArray();
        var results = new List<object>(ids.Length);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        foreach (var eventId in ids)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<EventStateRow>(new CommandDefinition(
                "SELECT status AS Status,version AS Version FROM business_event WHERE tenant_id=@TenantId AND event_id=@EventId FOR UPDATE",
                new { request.TenantId, EventId = eventId }, transaction, cancellationToken: cancellationToken));
            if (row is null)
            {
                results.Add(new { eventId, success = false, code = 40401, message = "事件不存在" });
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }
            var newVersion = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                UPDATE business_event SET triage_user_id=@TriageUserId,triage_user_name=@TriageUserName,
                  version=version+1,updated_at=CURRENT_TIMESTAMP
                WHERE tenant_id=@TenantId AND event_id=@EventId RETURNING version
                """, new { request.TenantId, EventId = eventId, request.TriageUserId, TriageUserName = CleanNullable(request.TriageUserName, 128) }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO business_event_activity(tenant_id,business_event_id,activity_type,to_status,detail_json,actor_user_id,actor_name,trace_id)
                VALUES(@TenantId,@EventId,'triage_assigned',@Status,jsonb_build_object('triageUserId',@TriageUserId,'triageUserName',@TriageUserName),
                  (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId)
                """, new { request.TenantId, EventId = eventId, row.Status, request.TriageUserId, request.TriageUserName, Actor = actor, TraceId = traceId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            results.Add(new { eventId, success = true, code = 0, version = newVersion });
        }
        return results;
    }

    public async Task<IReadOnlyList<DbTimelineItem>> GetEventTimelineAsync(long tenantId, long eventId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<DbTimelineItem>(new CommandDefinition(
            """
            SELECT activity_id AS ItemId,activity_type AS ItemType,from_status AS FromStatus,to_status AS ToStatus,
                   reason_code AS ReasonCode,detail_json::text AS DetailJson,actor_name AS ActorName,trace_id AS TraceId,created_at AS CreatedAt
            FROM business_event_activity WHERE tenant_id=@TenantId AND business_event_id=@EventId
            ORDER BY created_at,activity_id
            """, new { TenantId = tenantId, EventId = eventId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<ProductPage<DbIncidentCase>> GetCasesAsync(
        long tenantId,
        string? status,
        string? priority,
        string? keyword,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var args = new
        {
            TenantId = tenantId,
            Status = CleanNullable(status, 24),
            Priority = CleanNullable(priority, 16),
            Keyword = CleanNullable(keyword, 128),
            Offset = (page - 1) * pageSize,
            PageSize = pageSize
        };
        const string where = """
            WHERE c.tenant_id=@TenantId
              AND (@Status IS NULL OR c.status=@Status)
              AND (@Priority IS NULL OR c.priority=@Priority)
              AND (@Keyword IS NULL OR c.title ILIKE '%' || @Keyword || '%' OR c.case_no ILIKE '%' || @Keyword || '%')
            """;
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<DbIncidentCase>(new CommandDefinition(
            $"""
            SELECT c.case_id AS CaseId,c.tenant_id AS TenantId,c.case_no AS CaseNo,c.title AS Title,c.description AS Description,
                   c.status AS Status,c.status_reason AS StatusReason,c.priority AS Priority,c.owner_user_id AS OwnerUserId,
                   c.owner_name AS OwnerName,c.tags_json::text AS TagsJson,c.external_ticket_no AS ExternalTicketNo,
                   c.acknowledge_due_at AS AcknowledgeDueAt,c.start_due_at AS StartDueAt,c.resolve_due_at AS ResolveDueAt,
                   c.paused_at AS PausedAt,c.accumulated_pause_seconds AS AccumulatedPauseSeconds,
                   c.escalation_level AS EscalationLevel,c.resolution_json::text AS ResolutionJson,c.version AS Version,
                   c.created_at AS CreatedAt,c.updated_at AS UpdatedAt,c.closed_at AS ClosedAt,
                   (SELECT COUNT(*)::int FROM incident_case_event ce WHERE ce.case_id=c.case_id AND ce.active=TRUE) AS EventCount,
                   (SELECT COUNT(*)::int FROM incident_case_evidence ev WHERE ev.case_id=c.case_id) AS EvidenceCount
            FROM incident_case c {where}
            ORDER BY c.updated_at DESC,c.case_id DESC OFFSET @Offset LIMIT @PageSize
            """, args, cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM incident_case c {where}", args, cancellationToken: cancellationToken));
        return new(rows, page, pageSize, total);
    }

    public async Task<DbIncidentCase?> GetCaseAsync(long tenantId, long caseId, CancellationToken cancellationToken)
    {
        var page = await GetCasesByIdAsync(tenantId, caseId, cancellationToken);
        return page.SingleOrDefault();
    }

    private async Task<IReadOnlyList<DbIncidentCase>> GetCasesByIdAsync(long tenantId, long caseId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<DbIncidentCase>(new CommandDefinition(
            """
            SELECT c.case_id AS CaseId,c.tenant_id AS TenantId,c.case_no AS CaseNo,c.title AS Title,c.description AS Description,
                   c.status AS Status,c.status_reason AS StatusReason,c.priority AS Priority,c.owner_user_id AS OwnerUserId,
                   c.owner_name AS OwnerName,c.tags_json::text AS TagsJson,c.external_ticket_no AS ExternalTicketNo,
                   c.acknowledge_due_at AS AcknowledgeDueAt,c.start_due_at AS StartDueAt,c.resolve_due_at AS ResolveDueAt,
                   c.paused_at AS PausedAt,c.accumulated_pause_seconds AS AccumulatedPauseSeconds,
                   c.escalation_level AS EscalationLevel,c.resolution_json::text AS ResolutionJson,c.version AS Version,
                   c.created_at AS CreatedAt,c.updated_at AS UpdatedAt,c.closed_at AS ClosedAt,
                   (SELECT COUNT(*)::int FROM incident_case_event ce WHERE ce.case_id=c.case_id AND ce.active=TRUE) AS EventCount,
                   (SELECT COUNT(*)::int FROM incident_case_evidence ev WHERE ev.case_id=c.case_id) AS EvidenceCount
            FROM incident_case c WHERE c.tenant_id=@TenantId AND c.case_id=@CaseId
            """, new { TenantId = tenantId, CaseId = caseId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<ProductCommandResult> CreateCaseAsync(
        CaseCreateRequest request,
        string actor,
        string traceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var requestHash = Sha256(JsonSerializer.Serialize(request));
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var reserved = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO api_idempotency_record(tenant_id,command_scope,idempotency_key,request_hash,created_by,expires_at)
            VALUES(@TenantId,'case.create',@Key,@Hash,@Actor,CURRENT_TIMESTAMP+INTERVAL '24 hours')
            ON CONFLICT(tenant_id,command_scope,idempotency_key) DO NOTHING
            RETURNING idempotency_record_id
            """, new { request.TenantId, Key = idempotencyKey, Hash = requestHash, Actor = actor }, transaction, cancellationToken: cancellationToken));
        if (!reserved.HasValue)
        {
            var prior = await connection.QuerySingleAsync<IdempotencyRow>(new CommandDefinition(
                """
                SELECT request_hash AS RequestHash,response_json::text AS ResponseJson
                FROM api_idempotency_record WHERE tenant_id=@TenantId AND command_scope='case.create' AND idempotency_key=@Key
                """, new { request.TenantId, Key = idempotencyKey }, transaction, cancellationToken: cancellationToken));
            await transaction.RollbackAsync(cancellationToken);
            if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
                return new(ProductCommandStatus.Conflict, Message: "同一幂等键已用于不同请求");
            return new(ProductCommandStatus.Duplicate, JsonSerializer.Deserialize<JsonElement>(prior.ResponseJson), "请求已处理");
        }

        var eventIds = (request.EventIds ?? []).Distinct().Take(200).ToArray();
        if (eventIds.Length > 0)
        {
            var accessibleCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM business_event WHERE tenant_id=@TenantId AND event_id=ANY(@EventIds)",
                new { request.TenantId, EventIds = eventIds }, transaction, cancellationToken: cancellationToken));
            if (accessibleCount != eventIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(ProductCommandStatus.Invalid, Message: "部分事件不存在或不属于当前租户");
            }
        }

        var caseNo = $"CASE-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..22].ToUpperInvariant();
        var caseId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO incident_case(
              tenant_id,case_no,title,description,status,priority,owner_user_id,owner_name,tags_json,
              acknowledge_due_at,start_due_at,resolve_due_at)
            VALUES(@TenantId,@CaseNo,@Title,@Description,'new',@Priority,@OwnerUserId,@OwnerName,@Tags::jsonb,
              @AcknowledgeDueAt,@StartDueAt,@ResolveDueAt)
            RETURNING case_id
            """, new
            {
                request.TenantId,
                CaseNo = caseNo,
                Title = Clean(request.Title, "未命名案件", 256),
                Description = CleanNullable(request.Description, 8000),
                Priority = NormalizePriority(request.Priority),
                request.OwnerUserId,
                OwnerName = CleanNullable(request.OwnerName, 128),
                Tags = JsonSerializer.Serialize((request.Tags ?? []).Select(x => Clean(x, string.Empty, 64)).Where(x => x.Length > 0).Distinct()),
                request.AcknowledgeDueAt,
                request.StartDueAt,
                request.ResolveDueAt
            }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_activity(
              tenant_id,case_id,activity_type,to_status,detail_json,actor_user_id,actor_name,trace_id,idempotency_key)
            VALUES(@TenantId,@CaseId,'created','new','{}'::jsonb,
              (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId,@Key)
            """, new { request.TenantId, CaseId = caseId, Actor = actor, TraceId = traceId, Key = idempotencyKey }, transaction, cancellationToken: cancellationToken));

        foreach (var eventId in eventIds)
        {
            await LinkEventInternalAsync(connection, transaction, request.TenantId, caseId, eventId, "primary", "case_created", true, actor, traceId, cancellationToken);
        }

        var response = new { caseId, caseNo, status = "new", version = 1 };
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE api_idempotency_record SET response_json=@Response::jsonb WHERE idempotency_record_id=@Id",
            new { Response = JsonSerializer.Serialize(response), Id = reserved.Value }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(response);
    }

    public async Task<ProductCommandResult> TransitionCaseAsync(
        long tenantId,
        long caseId,
        CaseTransitionRequest request,
        bool canReview,
        string actor,
        string traceId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<CaseStateRow>(new CommandDefinition(
            "SELECT status AS Status,version AS Version,paused_at AS PausedAt FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId FOR UPDATE",
            new { TenantId = tenantId, CaseId = caseId }, transaction, cancellationToken: cancellationToken));
        if (current is null) return new(ProductCommandStatus.NotFound, Message: "案件不存在");
        if (current.Version != request.ExpectedVersion)
            return new(ProductCommandStatus.Conflict, Message: "案件已被其他用户更新", CurrentVersion: current.Version);
        if (!CaseStateMachine.TryValidate(current.Status, request.TargetStatus, request.ReasonCode, canReview, out var target, out var error))
            return new(ProductCommandStatus.Invalid, Message: error);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM incident_case_activity WHERE tenant_id=@TenantId AND case_id=@CaseId AND idempotency_key=@Key)",
                new { TenantId = tenantId, CaseId = caseId, Key = idempotencyKey }, transaction, cancellationToken: cancellationToken));
            if (exists) return new(ProductCommandStatus.Duplicate, new { caseId, status = current.Status, version = current.Version }, "请求已处理");
        }

        var nextVersion = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE incident_case SET
              status=@Target,status_reason=@Reason,
              paused_at=CASE WHEN @Target='paused' THEN CURRENT_TIMESTAMP WHEN @Current='paused' THEN NULL ELSE paused_at END,
              accumulated_pause_seconds=accumulated_pause_seconds+CASE WHEN @Current='paused' AND paused_at IS NOT NULL THEN EXTRACT(EPOCH FROM (CURRENT_TIMESTAMP-paused_at))::bigint ELSE 0 END,
              escalation_level=CASE WHEN @Target='escalated' THEN escalation_level+1 ELSE escalation_level END,
              acknowledged_at=CASE WHEN @Target='acknowledged' THEN COALESCE(acknowledged_at,CURRENT_TIMESTAMP) ELSE acknowledged_at END,
              started_at=CASE WHEN @Target='in_progress' THEN COALESCE(started_at,CURRENT_TIMESTAMP) ELSE started_at END,
              resolved_at=CASE WHEN @Target IN ('resolved','false_positive') THEN CURRENT_TIMESTAMP WHEN @Target='reopened' THEN NULL ELSE resolved_at END,
              resolution_json=CASE WHEN @Target IN ('resolved','false_positive','closed') THEN @Resolution::jsonb ELSE resolution_json END,
              closed_at=CASE WHEN @Target='closed' THEN CURRENT_TIMESTAMP WHEN @Target='reopened' THEN NULL ELSE closed_at END,
              version=version+1,updated_at=CURRENT_TIMESTAMP
            WHERE tenant_id=@TenantId AND case_id=@CaseId AND version=@ExpectedVersion RETURNING version
            """, new
            {
                TenantId = tenantId,
                CaseId = caseId,
                Target = target,
                Current = current.Status,
                Reason = CleanNullable(request.ReasonCode, 512),
                Resolution = request.Resolution?.GetRawText() ?? "{}",
                request.ExpectedVersion
            }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_activity(
              tenant_id,case_id,activity_type,from_status,to_status,reason_code,detail_json,actor_user_id,actor_name,trace_id,idempotency_key)
            VALUES(@TenantId,@CaseId,'state_transition',@FromStatus,@Target,@Reason,@Detail::jsonb,
              (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId,@Key)
            """, new
            {
                TenantId = tenantId,
                CaseId = caseId,
                FromStatus = current.Status,
                Target = target,
                Reason = CleanNullable(request.ReasonCode, 64),
                Detail = request.Resolution?.GetRawText() ?? "{}",
                Actor = actor,
                TraceId = traceId,
                Key = CleanNullable(idempotencyKey, 128)
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { caseId, status = target, version = nextVersion });
    }

    public async Task<ProductCommandResult> LinkCaseEventAsync(
        long tenantId,
        long caseId,
        CaseEventLinkRequest request,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var caseExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId)",
            new { TenantId = tenantId, CaseId = caseId }, transaction, cancellationToken: cancellationToken));
        var eventExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM business_event WHERE tenant_id=@TenantId AND event_id=@EventId)",
            new { TenantId = tenantId, request.EventId }, transaction, cancellationToken: cancellationToken));
        if (!caseExists || !eventExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ProductCommandStatus.NotFound, Message: "案件或事件不存在于当前租户");
        }
        await LinkEventInternalAsync(
            connection, transaction, tenantId, caseId, request.EventId,
            Clean(request.RelationType, "related", 32), CleanNullable(request.Reason, 512), request.Active,
            actor, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { caseId, eventId = request.EventId, active = request.Active });
    }

    public async Task<ProductCommandResult> AddCaseCommentAsync(
        long tenantId,
        long caseId,
        CaseCommentRequest request,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var commentId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO incident_case_comment(tenant_id,case_id,visibility,content,author_user_id,author_name)
            SELECT @TenantId,@CaseId,@Visibility,@Content,(SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor
            WHERE EXISTS(SELECT 1 FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId)
            RETURNING comment_id
            """, new
            {
                TenantId = tenantId,
                CaseId = caseId,
                Visibility = NormalizeVisibility(request.Visibility),
                Content = Clean(request.Content, string.Empty, 8000),
                Actor = actor
            }, transaction, cancellationToken: cancellationToken));
        if (!commentId.HasValue) return new(ProductCommandStatus.NotFound, Message: "案件不存在");
        await InsertCaseActivityAsync(connection, transaction, tenantId, caseId, "comment_added", null,
            JsonSerializer.Serialize(new { commentId }), actor, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { commentId });
    }

    public async Task<ProductCommandResult> AddCaseEvidenceAsync(
        long tenantId,
        long caseId,
        CaseEvidenceRequest request,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(request.Sha256)) return new(ProductCommandStatus.Invalid, Message: "证据 SHA-256 格式无效");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var evidenceId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO incident_case_evidence(
              tenant_id,case_id,evidence_type,source_type,source_id,object_key,sha256,media_type,masking_policy,purpose,added_by)
            SELECT @TenantId,@CaseId,@EvidenceType,@SourceType,@SourceId,@ObjectKey,@Sha256,@MediaType,@MaskingPolicy,@Purpose,@Actor
            WHERE EXISTS(SELECT 1 FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId)
            ON CONFLICT(case_id,sha256) DO UPDATE SET purpose=EXCLUDED.purpose
            RETURNING evidence_id
            """, new
            {
                TenantId = tenantId,
                CaseId = caseId,
                EvidenceType = Clean(request.EvidenceType, "reference", 32),
                SourceType = Clean(request.SourceType, "unknown", 64),
                SourceId = Clean(request.SourceId, "unknown", 256),
                ObjectKey = Clean(request.ObjectKey, string.Empty, 2048),
                Sha256 = request.Sha256.ToLowerInvariant(),
                MediaType = CleanNullable(request.MediaType, 128),
                MaskingPolicy = CleanNullable(request.MaskingPolicy, 128),
                Purpose = Clean(request.Purpose, "case_evidence", 256),
                Actor = actor
            }, transaction, cancellationToken: cancellationToken));
        if (!evidenceId.HasValue) return new(ProductCommandStatus.NotFound, Message: "案件不存在");
        await InsertCaseActivityAsync(connection, transaction, tenantId, caseId, "evidence_added", null,
            JsonSerializer.Serialize(new { evidenceId, request.SourceType, request.SourceId }), actor, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { evidenceId });
    }

    public async Task<IReadOnlyList<DbTimelineItem>> GetCaseTimelineAsync(long tenantId, long caseId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<DbTimelineItem>(new CommandDefinition(
            """
            SELECT activity_id AS ItemId,activity_type AS ItemType,from_status AS FromStatus,to_status AS ToStatus,
                   reason_code AS ReasonCode,detail_json::text AS DetailJson,actor_name AS ActorName,trace_id AS TraceId,created_at AS CreatedAt
            FROM incident_case_activity WHERE tenant_id=@TenantId AND case_id=@CaseId
            UNION ALL
            SELECT comment_id AS ItemId,'comment' AS ItemType,NULL,NULL,NULL,
                   jsonb_build_object('visibility',visibility,'content',content)::text AS DetailJson,author_name AS ActorName,NULL,created_at AS CreatedAt
            FROM incident_case_comment WHERE tenant_id=@TenantId AND case_id=@CaseId
            UNION ALL
            SELECT evidence_id AS ItemId,'evidence' AS ItemType,NULL,NULL,NULL,
                   jsonb_build_object('evidenceType',evidence_type,'sourceType',source_type,'sourceId',source_id,'sha256',sha256,'legalHold',legal_hold)::text,
                   added_by,NULL,created_at
            FROM incident_case_evidence WHERE tenant_id=@TenantId AND case_id=@CaseId
            UNION ALL
            SELECT notification_id AS ItemId,'notification' AS ItemType,NULL,NULL,status,
                   jsonb_build_object('channel',channel,'recipient',recipient_ref,'attemptCount',attempt_count)::text,
                   'system',NULL,created_at
            FROM notification_delivery WHERE tenant_id=@TenantId AND case_id=@CaseId
            ORDER BY CreatedAt,ItemId
            """, new { TenantId = tenantId, CaseId = caseId }, cancellationToken: cancellationToken))).AsList();
    }

    private static async Task LinkEventInternalAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long tenantId,
        long caseId,
        long eventId,
        string relationType,
        string? reason,
        bool active,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_event(tenant_id,case_id,event_id,relation_type,active,relation_reason,linked_by,unlinked_at)
            VALUES(@TenantId,@CaseId,@EventId,@RelationType,@Active,@Reason,@Actor,CASE WHEN @Active THEN NULL ELSE CURRENT_TIMESTAMP END)
            ON CONFLICT(case_id,event_id,relation_type) DO UPDATE SET
              active=EXCLUDED.active,relation_reason=EXCLUDED.relation_reason,linked_by=EXCLUDED.linked_by,
              linked_at=CASE WHEN EXCLUDED.active THEN CURRENT_TIMESTAMP ELSE incident_case_event.linked_at END,
              unlinked_at=CASE WHEN EXCLUDED.active THEN NULL ELSE CURRENT_TIMESTAMP END
            """, new { TenantId = tenantId, CaseId = caseId, EventId = eventId, RelationType = relationType, Active = active, Reason = reason, Actor = actor }, transaction, cancellationToken: cancellationToken));
        await InsertCaseActivityAsync(connection, transaction, tenantId, caseId, active ? "event_linked" : "event_unlinked", reason,
            JsonSerializer.Serialize(new { eventId, relationType }), actor, traceId, cancellationToken);

        var eventState = active ? "linked" : await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS(SELECT 1 FROM incident_case_event WHERE tenant_id=@TenantId AND event_id=@EventId AND active=TRUE)
              THEN 'linked' ELSE 'acknowledged' END
            """, new { TenantId = tenantId, EventId = eventId }, transaction, cancellationToken: cancellationToken));
        var prior = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM business_event WHERE tenant_id=@TenantId AND event_id=@EventId FOR UPDATE",
            new { TenantId = tenantId, EventId = eventId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE business_event SET status=@Status,version=version+1,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND event_id=@EventId",
            new { TenantId = tenantId, EventId = eventId, Status = eventState }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO business_event_activity(tenant_id,business_event_id,activity_type,from_status,to_status,reason_code,detail_json,actor_user_id,actor_name,trace_id)
            VALUES(@TenantId,@EventId,@Activity,@Prior,@Status,@Reason,jsonb_build_object('caseId',@CaseId,'relationType',@RelationType),
              (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId)
            """, new { TenantId = tenantId, EventId = eventId, Activity = active ? "case_linked" : "case_unlinked", Prior = prior, Status = eventState, Reason = reason, CaseId = caseId, RelationType = relationType, Actor = actor, TraceId = traceId }, transaction, cancellationToken: cancellationToken));
    }

    private static Task InsertCaseActivityAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long tenantId,
        long caseId,
        string activityType,
        string? reason,
        string detailJson,
        string actor,
        string traceId,
        CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_activity(tenant_id,case_id,activity_type,reason_code,detail_json,actor_user_id,actor_name,trace_id)
            VALUES(@TenantId,@CaseId,@ActivityType,@Reason,@Detail::jsonb,
              (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId)
            """, new { TenantId = tenantId, CaseId = caseId, ActivityType = activityType, Reason = CleanNullable(reason, 64), Detail = detailJson, Actor = actor, TraceId = traceId }, transaction, cancellationToken: cancellationToken));

    private static string Clean(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanNullable(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Clean(value, string.Empty, maxLength);

    private static string NormalizeSeverity(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "high" => "high",
        "critical" => "critical",
        _ => "medium"
    };

    private static string NormalizePriority(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "high" => "high",
        "urgent" => "urgent",
        _ => "normal"
    };

    private static string NormalizeVisibility(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "private" => "private",
        "participants" => "participants",
        _ => "tenant"
    };

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record EventUpsertRow(long EventId, string EventNo, string Status, int Version, bool Inserted);
    private sealed record EventStateRow(string Status, int Version);
    private sealed record CaseStateRow(string Status, int Version, DateTimeOffset? PausedAt);
    private sealed record IdempotencyRow(string RequestHash, string ResponseJson);
}

internal sealed record DbTimelineItem(
    long ItemId,
    string ItemType,
    string? FromStatus,
    string? ToStatus,
    string? ReasonCode,
    string DetailJson,
    string ActorName,
    string? TraceId,
    DateTimeOffset CreatedAt);
