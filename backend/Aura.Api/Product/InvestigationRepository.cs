using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class InvestigationRepository(PgSqlConnectionFactory connectionFactory)
{
    public async Task<object> CreateAsync(InvestigationCreateRequest request, string actor, CancellationToken cancellationToken)
    {
        var number = $"INV-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO investigation_session(tenant_id,investigation_no,title,owner_user_id,owner_name)
            VALUES(@TenantId,@Number,@Title,(SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor)
            RETURNING investigation_id
            """, new
            {
                request.TenantId,
                Number = number,
                Title = Clean(request.Title, "未命名调查", 256),
                Actor = actor
            }, cancellationToken: cancellationToken));
        return new { investigationId = id, investigationNo = number, status = "active", version = 1 };
    }

    public async Task<object?> GetAsync(long tenantId, long investigationId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var session = await connection.QuerySingleOrDefaultAsync<DbInvestigation>(new CommandDefinition(
            """
            SELECT investigation_id AS InvestigationId,tenant_id AS TenantId,investigation_no AS InvestigationNo,
                   title AS Title,status AS Status,owner_user_id AS OwnerUserId,owner_name AS OwnerName,
                   shared_with_json::text AS SharedWithJson,version AS Version,created_at AS CreatedAt,updated_at AS UpdatedAt
            FROM investigation_session WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId
            """, new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken));
        if (session is null) return null;
        var queries = (await connection.QueryAsync<DbInvestigationQuery>(new CommandDefinition(
            """
            SELECT query_id AS QueryId,query_type AS QueryType,query_json::text AS QueryJson,model_code AS ModelCode,
                   model_version AS ModelVersion,threshold_policy_version AS ThresholdPolicyVersion,data_version AS DataVersion,
                   status AS Status,result_json::text AS ResultJson,created_by AS CreatedBy,started_at AS StartedAt,
                   completed_at AS CompletedAt,created_at AS CreatedAt
            FROM investigation_query_snapshot WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId
            ORDER BY query_id DESC LIMIT 50
            """, new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken))).AsList();
        var evidence = (await connection.QueryAsync<DbInvestigationEvidence>(new CommandDefinition(
            """
            SELECT item_id AS ItemId,source_type AS SourceType,source_id AS SourceId,sha256 AS Sha256,
                   note AS Note,added_by AS AddedBy,created_at AS CreatedAt
            FROM investigation_evidence_item WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId
            ORDER BY item_id DESC
            """, new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken))).AsList();
        return new { session, queries, evidence };
    }

    public async Task<long?> StartQueryAsync(
        long tenantId,
        long investigationId,
        InvestigationQueryRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO investigation_query_snapshot(
              tenant_id,investigation_id,query_type,query_json,model_code,model_version,
              threshold_policy_version,data_version,status,created_by,started_at)
            SELECT @TenantId,@InvestigationId,@QueryType,@Query::jsonb,@ModelCode,@ModelVersion,
              @ThresholdPolicyVersion,@DataVersion,'running',@Actor,CURRENT_TIMESTAMP
            WHERE EXISTS(SELECT 1 FROM investigation_session WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId AND status='active')
            RETURNING query_id
            """, new
            {
                TenantId = tenantId,
                InvestigationId = investigationId,
                QueryType = Clean(request.QueryType, "timeline", 32),
                Query = request.Query.GetRawText(),
                request.ModelCode,
                request.ModelVersion,
                request.ThresholdPolicyVersion,
                request.DataVersion,
                Actor = actor
            }, cancellationToken: cancellationToken));
    }

    public async Task CompleteQueryAsync(
        long tenantId,
        long queryId,
        string status,
        string resultJson,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE investigation_query_snapshot SET status=@Status,result_json=@Result::jsonb,completed_at=CURRENT_TIMESTAMP
            WHERE tenant_id=@TenantId AND query_id=@QueryId
            """, new { TenantId = tenantId, QueryId = queryId, Status = status, Result = resultJson }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<InvestigationTimelineItem>> GetTimelineAsync(
        long tenantId,
        long investigationId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM investigation_session WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId)",
            new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken));
        if (!exists) return [];
        return (await connection.QueryAsync<InvestigationTimelineItem>(new CommandDefinition(
            """
            SELECT * FROM (
              SELECT capture_id::text AS ItemId,'capture' AS ItemType,capture_time AS OccurredAt,
                     jsonb_build_object('deviceId',device_id,'channelNo',channel_no,'imagePath',image_path,'metadata',metadata_json)::text AS DetailJson
              FROM capture_record WHERE tenant_id=@TenantId AND (@From IS NULL OR capture_time>=@From) AND (@To IS NULL OR capture_time<=@To)
              UNION ALL
              SELECT event_id::text,'business_event',last_occurred_at,
                     jsonb_build_object('eventNo',event_no,'title',title,'severity',severity,'status',status,'occurrences',occurrence_count)::text
              FROM business_event WHERE tenant_id=@TenantId AND (@From IS NULL OR last_occurred_at>=@From) AND (@To IS NULL OR last_occurred_at<=@To)
              UNION ALL
              SELECT ('case-activity-'||a.activity_id)::text,'case_activity',a.created_at,
                     jsonb_build_object('caseId',a.case_id,'caseNo',c.case_no,'activity',a.activity_type,'from',a.from_status,'to',a.to_status,'reason',a.reason_code)::text
              FROM incident_case_activity a JOIN incident_case c ON c.case_id=a.case_id AND c.tenant_id=a.tenant_id
              WHERE a.tenant_id=@TenantId AND (@From IS NULL OR a.created_at>=@From) AND (@To IS NULL OR a.created_at<=@To)
            ) timeline ORDER BY OccurredAt DESC LIMIT @Limit
            """, new { TenantId = tenantId, InvestigationId = investigationId, From = from, To = to, Limit = limit }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<object?> GetEvidenceGraphAsync(long tenantId, long investigationId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM investigation_session WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId)",
            new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken));
        if (!exists) return null;
        var nodes = (await connection.QueryAsync<InvestigationGraphNode>(new CommandDefinition(
            """
            SELECT ('evidence:'||item_id) AS Id,source_type AS Type,source_id AS Label,'candidate' AS EvidenceState
            FROM investigation_evidence_item WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId
            UNION ALL
            SELECT ('case:'||c.case_id),'case',c.case_no,'confirmed'
            FROM incident_case c
            WHERE c.tenant_id=@TenantId AND EXISTS(
              SELECT 1 FROM incident_case_evidence ce
              JOIN investigation_evidence_item ie ON ie.tenant_id=ce.tenant_id AND ie.sha256=ce.sha256
              WHERE ce.case_id=c.case_id AND ie.investigation_id=@InvestigationId)
            """, new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken))).AsList();
        var edges = (await connection.QueryAsync<InvestigationGraphEdge>(new CommandDefinition(
            """
            SELECT ('evidence:'||ie.item_id) AS Source,('case:'||ce.case_id) AS Target,
                   'attached_to' AS Relation,ce.created_at AS OccurredAt,'confirmed' AS EvidenceState
            FROM investigation_evidence_item ie
            JOIN incident_case_evidence ce ON ce.tenant_id=ie.tenant_id AND ce.sha256=ie.sha256
            WHERE ie.tenant_id=@TenantId AND ie.investigation_id=@InvestigationId
            """, new { TenantId = tenantId, InvestigationId = investigationId }, cancellationToken: cancellationToken))).AsList();
        return new { nodes, edges };
    }

    public async Task<long?> AddEvidenceAsync(
        long tenantId,
        long investigationId,
        InvestigationEvidenceRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(request.Sha256)) return null;
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO investigation_evidence_item(tenant_id,investigation_id,source_type,source_id,sha256,note,added_by)
            SELECT @TenantId,@InvestigationId,@SourceType,@SourceId,@Sha256,@Note,@Actor
            WHERE EXISTS(SELECT 1 FROM investigation_session WHERE tenant_id=@TenantId AND investigation_id=@InvestigationId AND status='active')
            ON CONFLICT(investigation_id,source_type,source_id) DO UPDATE SET note=EXCLUDED.note
            RETURNING item_id
            """, new
            {
                TenantId = tenantId,
                InvestigationId = investigationId,
                SourceType = Clean(request.SourceType, "unknown", 64),
                SourceId = Clean(request.SourceId, "unknown", 256),
                Sha256 = request.Sha256.ToLowerInvariant(),
                Note = CleanNullable(request.Note, 4000),
                Actor = actor
            }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> AttachToCaseAsync(
        long tenantId,
        long investigationId,
        InvestigationAttachRequest request,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        var ids = request.EvidenceItemIds.Distinct().Take(200).ToArray();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var caseExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId)",
            new { TenantId = tenantId, request.CaseId }, transaction, cancellationToken: cancellationToken));
        if (!caseExists) return new(ProductCommandStatus.NotFound, Message: "案件不存在");
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_evidence(
              tenant_id,case_id,evidence_type,source_type,source_id,object_key,sha256,purpose,added_by)
            SELECT ie.tenant_id,@CaseId,'investigation_reference',ie.source_type,ie.source_id,
                   'investigation:'||ie.investigation_id||':'||ie.source_type||':'||ie.source_id,ie.sha256,'case_investigation',@Actor
            FROM investigation_evidence_item ie
            WHERE ie.tenant_id=@TenantId AND ie.investigation_id=@InvestigationId AND ie.item_id=ANY(@Ids)
            ON CONFLICT(case_id,sha256) DO NOTHING
            """, new { TenantId = tenantId, InvestigationId = investigationId, request.CaseId, Ids = ids, Actor = actor }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_activity(tenant_id,case_id,activity_type,detail_json,actor_user_id,actor_name,trace_id)
            VALUES(@TenantId,@CaseId,'investigation_attached',jsonb_build_object('investigationId',@InvestigationId,'evidenceCount',@Inserted),
              (SELECT user_id FROM sys_user WHERE user_name=@Actor LIMIT 1),@Actor,@TraceId)
            """, new { TenantId = tenantId, request.CaseId, InvestigationId = investigationId, Inserted = inserted, Actor = actor, TraceId = traceId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { request.CaseId, investigationId, attachedCount = inserted });
    }

    private static string Clean(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? CleanNullable(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : Clean(value, string.Empty, maxLength);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal sealed record DbInvestigation(
    long InvestigationId,long TenantId,string InvestigationNo,string Title,string Status,long? OwnerUserId,
    string OwnerName,string SharedWithJson,int Version,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
internal sealed record DbInvestigationQuery(
    long QueryId,string QueryType,string QueryJson,string? ModelCode,string? ModelVersion,int? ThresholdPolicyVersion,
    string? DataVersion,string Status,string ResultJson,string CreatedBy,DateTimeOffset? StartedAt,DateTimeOffset? CompletedAt,DateTimeOffset CreatedAt);
internal sealed record DbInvestigationEvidence(long ItemId,string SourceType,string SourceId,string Sha256,string? Note,string AddedBy,DateTimeOffset CreatedAt);
internal sealed record InvestigationTimelineItem(string ItemId,string ItemType,DateTimeOffset OccurredAt,string DetailJson);
internal sealed record InvestigationGraphNode(string Id,string Type,string Label,string EvidenceState);
internal sealed record InvestigationGraphEdge(string Source,string Target,string Relation,DateTimeOffset OccurredAt,string EvidenceState);

