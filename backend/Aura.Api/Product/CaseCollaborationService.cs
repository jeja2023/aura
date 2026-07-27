using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class CaseCollaborationService(PgSqlConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<object>> ListParticipantsAsync(long tenantId, long caseId, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT p.participant_id AS participantId,p.role_type AS roleType,p.user_id AS userId,
              u.user_name AS userName,p.joined_at AS joinedAt,p.removed_at AS removedAt
            FROM incident_case_participant p
            LEFT JOIN sys_user u ON u.user_id=p.user_id
            WHERE p.tenant_id=@TenantId AND p.case_id=@CaseId AND p.removed_at IS NULL
            ORDER BY p.joined_at,p.participant_id
            """, new { TenantId = tenantId, CaseId = caseId }, cancellationToken: ct));
        return rows.Cast<object>().ToArray();
    }

    public async Task<ProductCommandResult> AddParticipantAsync(
        long caseId, CaseParticipantRequest request, string actor, string traceId, CancellationToken ct)
    {
        var role = request.RoleType.Trim().ToLowerInvariant();
        if (role is not ("owner" or "assignee" or "coordinator" or "watcher"))
            return new(ProductCommandStatus.Invalid, Message: "Unsupported participant role");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(SELECT 1 FROM incident_case c WHERE c.tenant_id=@TenantId AND c.case_id=@CaseId)
              AND EXISTS(SELECT 1 FROM sys_user u WHERE u.user_id=@UserId)
            """, new { request.TenantId, request.UserId, CaseId = caseId }, tx, cancellationToken: ct));
        if (!exists) return new(ProductCommandStatus.NotFound, Message: "Case or user not found");

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_participant(tenant_id,case_id,role_type,user_id)
            VALUES(@TenantId,@CaseId,@Role,@UserId)
            ON CONFLICT(case_id,role_type,user_id) WHERE removed_at IS NULL DO UPDATE SET removed_at=NULL
            """, new { request.TenantId, CaseId = caseId, Role = role, request.UserId }, tx, cancellationToken: ct));
        await AddActivityAsync(connection, tx, request.TenantId, caseId, "participant_added",
            new { request.UserId, role }, actor, traceId, ct);
        await tx.CommitAsync(ct);
        return ProductCommandResult.Ok(new { caseId, request.UserId, role });
    }

    public async Task<ProductCommandResult> RemoveParticipantAsync(
        long tenantId, long caseId, long userId, string roleType, string actor, string traceId, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var count = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE incident_case_participant SET removed_at=CURRENT_TIMESTAMP
            WHERE tenant_id=@TenantId AND case_id=@CaseId AND user_id=@UserId
              AND role_type=@Role AND removed_at IS NULL
            """, new { TenantId = tenantId, CaseId = caseId, UserId = userId, Role = roleType.Trim().ToLowerInvariant() }, tx, cancellationToken: ct));
        if (count == 0) return new(ProductCommandStatus.NotFound, Message: "Active participant not found");
        await AddActivityAsync(connection, tx, tenantId, caseId, "participant_removed",
            new { userId, roleType }, actor, traceId, ct);
        await tx.CommitAsync(ct);
        return ProductCommandResult.Ok(new { caseId, userId, roleType, removed = true });
    }

    public async Task<IReadOnlyList<CaseTemplateRow>> ListTemplatesAsync(long tenantId, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<CaseTemplateRow>(new CommandDefinition(
            """
            SELECT template_id AS TemplateId,tenant_id AS TenantId,template_code AS TemplateCode,version AS Version,
              name AS Name,event_type AS EventType,default_priority AS DefaultPriority,default_sla_json::text AS DefaultSlaJson,
              checklist_json::text AS ChecklistJson,required_evidence_json::text AS RequiredEvidenceJson,status AS Status,
              created_by AS CreatedBy,approved_by AS ApprovedBy,created_at AS CreatedAt,updated_at AS UpdatedAt
            FROM incident_case_template WHERE tenant_id=@TenantId ORDER BY template_code,version DESC
            """, new { TenantId = tenantId }, cancellationToken: ct))).AsList();
    }

    public async Task<ProductCommandResult> SaveTemplateAsync(CaseTemplateWriteRequest request, string actor, CancellationToken ct)
    {
        if (request.Checklist.ValueKind != JsonValueKind.Array || request.RequiredEvidence.ValueKind != JsonValueKind.Array)
            return new(ProductCommandStatus.Invalid, Message: "Template checklist and requiredEvidence must be arrays");
        var priority = request.DefaultPriority.Trim().ToLowerInvariant();
        if (priority is not ("low" or "normal" or "high" or "urgent"))
            return new(ProductCommandStatus.Invalid, Message: "Template priority is invalid");
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO incident_case_template(tenant_id,template_code,version,name,event_type,default_priority,
              default_sla_json,checklist_json,required_evidence_json,status,created_by)
            VALUES(@TenantId,@Code,@Version,@Name,@EventType,@Priority,@Sla::jsonb,@Checklist::jsonb,@Evidence::jsonb,'draft',@Actor)
            ON CONFLICT(tenant_id,template_code,version) DO UPDATE SET
              name=EXCLUDED.name,event_type=EXCLUDED.event_type,default_priority=EXCLUDED.default_priority,
              default_sla_json=EXCLUDED.default_sla_json,checklist_json=EXCLUDED.checklist_json,
              required_evidence_json=EXCLUDED.required_evidence_json,status='draft',updated_at=CURRENT_TIMESTAMP
            RETURNING template_id
            """, new
            {
                request.TenantId, Code = Clean(request.TemplateCode, 128), request.Version,
                Name = Clean(request.Name, 256), EventType = CleanNullable(request.EventType, 128), Priority = priority,
                Sla = request.DefaultSla?.GetRawText() ?? "{}", Checklist = request.Checklist.GetRawText(),
                Evidence = request.RequiredEvidence.GetRawText(), Actor = actor
            }, cancellationToken: ct));
        return ProductCommandResult.Ok(new { templateId = id, status = "draft" });
    }

    public async Task<ProductCommandResult> TransitionTemplateAsync(
        long tenantId, long templateId, CaseTemplateStateRequest request, string actor, CancellationToken ct)
    {
        var target = request.TargetStatus.Trim().ToLowerInvariant();
        if (target is not ("active" or "retired" or "draft"))
            return new(ProductCommandStatus.Invalid, Message: "Template status is invalid");
        await using var connection = connectionFactory.CreateConnection();
        var count = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE incident_case_template SET status=@Target,approved_by=CASE WHEN @Target='active' THEN @Actor ELSE approved_by END,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND template_id=@Id",
            new { TenantId = tenantId, Id = templateId, Target = target, Actor = actor }, cancellationToken: ct));
        return count == 0 ? new(ProductCommandStatus.NotFound, Message: "Case template not found") : ProductCommandResult.Ok(new { templateId, status = target });
    }

    public async Task<ProductCommandResult> ApplyTemplateAsync(long tenantId, long caseId, long templateId, string actor, string traceId, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var template = await connection.QuerySingleOrDefaultAsync<CaseTemplateRow>(new CommandDefinition(
            "SELECT template_id AS TemplateId,tenant_id AS TenantId,template_code AS TemplateCode,version AS Version,name AS Name,event_type AS EventType,default_priority AS DefaultPriority,default_sla_json::text AS DefaultSlaJson,checklist_json::text AS ChecklistJson,required_evidence_json::text AS RequiredEvidenceJson,status AS Status,created_by AS CreatedBy,approved_by AS ApprovedBy,created_at AS CreatedAt,updated_at AS UpdatedAt FROM incident_case_template WHERE tenant_id=@TenantId AND template_id=@Id AND status='active'",
            new { TenantId = tenantId, Id = templateId }, tx, cancellationToken: ct));
        if (template is null) return new(ProductCommandStatus.NotFound, Message: "Active case template not found");
        var caseExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId)", new { TenantId = tenantId, CaseId = caseId }, tx, cancellationToken: ct));
        if (!caseExists) return new(ProductCommandStatus.NotFound, Message: "Case not found");
        using var document = JsonDocument.Parse(template.ChecklistJson);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var code = item.TryGetProperty("code", out var codeNode) ? codeNode.GetString() : null;
            var title = item.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title)) continue;
            var required = !item.TryGetProperty("required", out var requiredNode) || requiredNode.ValueKind != JsonValueKind.False;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO incident_case_checklist_item(tenant_id,case_id,item_code,title,required) VALUES(@TenantId,@CaseId,@Code,@Title,@Required) ON CONFLICT(case_id,item_code) DO NOTHING",
                new { TenantId = tenantId, CaseId = caseId, Code = Clean(code, 128), Title = Clean(title, 256), Required = required }, tx, cancellationToken: ct));
        }
        await AddActivityAsync(connection, tx, tenantId, caseId, "template_applied", new { templateId, template.TemplateCode }, actor, traceId, ct);
        await tx.CommitAsync(ct);
        return ProductCommandResult.Ok(new { caseId, templateId, template.TemplateCode });
    }

    public async Task<IReadOnlyList<object>> ListChecklistAsync(long tenantId, long caseId, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync(new CommandDefinition(
            "SELECT checklist_item_id AS checklistItemId,item_code AS itemCode,title,required,status,completed_by AS completedBy,completed_at AS completedAt,detail_json AS detail FROM incident_case_checklist_item WHERE tenant_id=@TenantId AND case_id=@CaseId ORDER BY checklist_item_id",
            new { TenantId = tenantId, CaseId = caseId }, cancellationToken: ct));
        return rows.Cast<object>().ToArray();
    }

    public async Task<ProductCommandResult> UpdateChecklistAsync(long tenantId, long caseId, long itemId, CaseChecklistUpdateRequest request, string actor, string traceId, CancellationToken ct)
    {
        var target = request.Status.Trim().ToLowerInvariant();
        if (target is not ("pending" or "in_progress" or "completed" or "skipped"))
            return new(ProductCommandStatus.Invalid, Message: "Checklist status is invalid");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var count = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE incident_case_checklist_item SET status=@Target,completed_by=CASE WHEN @Target='completed' THEN @Actor ELSE completed_by END,completed_at=CASE WHEN @Target='completed' THEN CURRENT_TIMESTAMP ELSE completed_at END,detail_json=COALESCE(@Detail::jsonb,detail_json),updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@CaseId AND checklist_item_id=@ItemId",
            new { TenantId = tenantId, CaseId = caseId, ItemId = itemId, Target = target, Actor = actor, Detail = request.Detail?.GetRawText() }, tx, cancellationToken: ct));
        if (count == 0) return new(ProductCommandStatus.NotFound, Message: "Checklist item not found");
        await AddActivityAsync(connection, tx, tenantId, caseId, "checklist_updated", new { itemId, status = target }, actor, traceId, ct);
        await tx.CommitAsync(ct);
        return ProductCommandResult.Ok(new { caseId, itemId, status = target });
    }

    public async Task<ProductCommandResult> MergeAsync(long sourceCaseId, CaseMergeRequest request, string actor, string traceId, string idempotencyKey, CancellationToken ct)
    {
        if (request.TargetCaseId <= 0 || request.TargetCaseId == sourceCaseId) return new(ProductCommandStatus.Invalid, Message: "Target case is invalid");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var requestHash = Hash(JsonSerializer.Serialize(new { sourceCaseId, request }));
        var reservation = await ReserveIdempotencyAsync(connection, tx, request.TenantId, "case.merge", idempotencyKey, requestHash, actor, ct);
        if (!reservation.Created)
        {
            await tx.RollbackAsync(ct);
            if (!string.Equals(reservation.RequestHash, requestHash, StringComparison.Ordinal))
                return new(ProductCommandStatus.Conflict, Message: "The idempotency key was already used for a different merge request");
            return new(ProductCommandStatus.Duplicate, JsonSerializer.Deserialize<JsonElement>(reservation.ResponseJson!), "Merge request already processed");
        }
        var rows = (await connection.QueryAsync<CaseVersionRow>(new CommandDefinition(
            "SELECT case_id AS CaseId,status AS Status,version AS Version FROM incident_case WHERE tenant_id=@TenantId AND case_id=ANY(@Ids) FOR UPDATE",
            new { request.TenantId, Ids = new[] { sourceCaseId, request.TargetCaseId } }, tx, cancellationToken: ct))).AsList();
        var source = rows.SingleOrDefault(x => x.CaseId == sourceCaseId);
        var target = rows.SingleOrDefault(x => x.CaseId == request.TargetCaseId);
        if (source is null || target is null) return new(ProductCommandStatus.NotFound, Message: "Source or target case not found");
        if (source.Version != request.ExpectedSourceVersion) return new(ProductCommandStatus.Conflict, Message: "Source case changed", CurrentVersion: source.Version);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO incident_case_event(tenant_id,case_id,event_id,relation_type,active,relation_reason,linked_by)
            SELECT tenant_id,@TargetCaseId,event_id,relation_type,TRUE,'case_merged',@Actor
            FROM incident_case_event WHERE tenant_id=@TenantId AND case_id=@SourceCaseId AND active
            ON CONFLICT(case_id,event_id,relation_type) DO UPDATE SET active=TRUE,unlinked_at=NULL
            """, new { request.TenantId, SourceCaseId = sourceCaseId, request.TargetCaseId, Actor = actor }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE incident_case_event SET active=FALSE,unlinked_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@SourceCaseId AND active",
            new { request.TenantId, SourceCaseId = sourceCaseId }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO incident_case_relation(tenant_id,source_case_id,target_case_id,relation_type,reason,created_by) VALUES(@TenantId,@SourceCaseId,@TargetCaseId,'merged_into',@Reason,@Actor) ON CONFLICT DO NOTHING",
            new { request.TenantId, SourceCaseId = sourceCaseId, request.TargetCaseId, request.Reason, Actor = actor }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE incident_case SET status='closed',status_reason='merged_into',resolution_json=jsonb_build_object('mergedIntoCaseId',@TargetCaseId,'reason',@Reason),version=version+1,closed_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@SourceCaseId",
            new { request.TenantId, SourceCaseId = sourceCaseId, request.TargetCaseId, request.Reason }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE incident_case SET version=version+1,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@TargetCaseId", new { request.TenantId, request.TargetCaseId }, tx, cancellationToken: ct));
        await AddActivityAsync(connection, tx, request.TenantId, sourceCaseId, "merged_into", new { targetCaseId = request.TargetCaseId, request.Reason }, actor, traceId, ct);
        await AddActivityAsync(connection, tx, request.TenantId, request.TargetCaseId, "case_merged", new { sourceCaseId, request.Reason }, actor, traceId, ct);
        var response = new { sourceCaseId, targetCaseId = request.TargetCaseId, status = "merged" };
        await StoreIdempotentResponseAsync(connection, tx, reservation.Id!.Value, response, ct);
        await tx.CommitAsync(ct);
        return ProductCommandResult.Ok(response);
    }

    public async Task<ProductCommandResult> SplitAsync(long sourceCaseId, CaseSplitRequest request, string actor, string traceId, string idempotencyKey, CancellationToken ct)
    {
        var eventIds = request.EventIds.Distinct().Take(200).ToArray();
        if (eventIds.Length == 0) return new(ProductCommandStatus.Invalid, Message: "At least one event is required for split");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var requestHash = Hash(JsonSerializer.Serialize(new { sourceCaseId, request }));
        var reservation = await ReserveIdempotencyAsync(connection, tx, request.TenantId, "case.split", idempotencyKey, requestHash, actor, ct);
        if (!reservation.Created)
        {
            await tx.RollbackAsync(ct);
            if (!string.Equals(reservation.RequestHash, requestHash, StringComparison.Ordinal))
                return new(ProductCommandStatus.Conflict, Message: "The idempotency key was already used for a different split request");
            return new(ProductCommandStatus.Duplicate, JsonSerializer.Deserialize<JsonElement>(reservation.ResponseJson!), "Split request already processed");
        }
        var source = await connection.QuerySingleOrDefaultAsync<CaseVersionRow>(new CommandDefinition(
            "SELECT case_id AS CaseId,status AS Status,version AS Version FROM incident_case WHERE tenant_id=@TenantId AND case_id=@CaseId FOR UPDATE",
            new { request.TenantId, CaseId = sourceCaseId }, tx, cancellationToken: ct));
        if (source is null) return new(ProductCommandStatus.NotFound, Message: "Source case not found");
        if (source.Version != request.ExpectedSourceVersion) return new(ProductCommandStatus.Conflict, Message: "Source case changed", CurrentVersion: source.Version);
        var accessible = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM incident_case_event WHERE tenant_id=@TenantId AND case_id=@CaseId AND event_id=ANY(@EventIds) AND active",
            new { request.TenantId, CaseId = sourceCaseId, EventIds = eventIds }, tx, cancellationToken: ct));
        if (accessible != eventIds.Length) return new(ProductCommandStatus.Invalid, Message: "Selected events are not active in the source case");
        var caseNo = $"CASE-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..22].ToUpperInvariant();
        var newId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO incident_case(tenant_id,case_no,title,description,status,priority) SELECT tenant_id,@CaseNo,@Title,@Description,'new',@Priority FROM incident_case WHERE tenant_id=@TenantId AND case_id=@SourceCaseId RETURNING case_id",
            new { request.TenantId, CaseNo = caseNo, Title = Clean(request.Title, 256), Description = CleanNullable(request.Description, 8000), Priority = NormalizePriority(request.Priority), SourceCaseId = sourceCaseId }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO incident_case_event(tenant_id,case_id,event_id,relation_type,active,relation_reason,linked_by) SELECT tenant_id,@NewId,event_id,relation_type,TRUE,'case_split',@Actor FROM incident_case_event WHERE tenant_id=@TenantId AND case_id=@SourceCaseId AND event_id=ANY(@EventIds) AND active",
            new { request.TenantId, NewId = newId, SourceCaseId = sourceCaseId, EventIds = eventIds, Actor = actor }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE incident_case_event SET active=FALSE,unlinked_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@SourceCaseId AND event_id=ANY(@EventIds) AND active",
            new { request.TenantId, SourceCaseId = sourceCaseId, EventIds = eventIds }, tx, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO incident_case_relation(tenant_id,source_case_id,target_case_id,relation_type,reason,created_by) VALUES(@TenantId,@NewId,@SourceCaseId,'split_from',@Reason,@Actor)",
            new { request.TenantId, NewId = newId, SourceCaseId = sourceCaseId, request.Reason, Actor = actor }, tx, cancellationToken: ct));
        await AddActivityAsync(connection, tx, request.TenantId, newId, "created_by_split", new { sourceCaseId, eventIds }, actor, traceId, ct);
        await AddActivityAsync(connection, tx, request.TenantId, sourceCaseId, "split_created", new { newCaseId = newId, eventIds, request.Reason }, actor, traceId, ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE incident_case SET version=version+1,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@SourceCaseId", new { request.TenantId, SourceCaseId = sourceCaseId }, tx, cancellationToken: ct));
        var response = new { caseId = newId, caseNo, sourceCaseId, status = "new" };
        await StoreIdempotentResponseAsync(connection, tx, reservation.Id!.Value, response, ct);
        await tx.CommitAsync(ct);
        return ProductCommandResult.Ok(response);
    }

    private static async Task<IdempotencyReservation> ReserveIdempotencyAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction tx,
        long tenantId,
        string scope,
        string key,
        string requestHash,
        string actor,
        CancellationToken ct)
    {
        var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO api_idempotency_record(tenant_id,command_scope,idempotency_key,request_hash,created_by,expires_at)
            VALUES(@TenantId,@Scope,@Key,@Hash,@Actor,CURRENT_TIMESTAMP+INTERVAL '24 hours')
            ON CONFLICT(tenant_id,command_scope,idempotency_key) DO NOTHING
            RETURNING idempotency_record_id
            """, new { TenantId = tenantId, Scope = scope, Key = key, Hash = requestHash, Actor = actor }, tx, cancellationToken: ct));
        if (id.HasValue) return new(true, id, requestHash, null);
        var prior = await connection.QuerySingleAsync<IdempotencyPrior>(new CommandDefinition(
            """
            SELECT request_hash AS RequestHash,response_json::text AS ResponseJson
            FROM api_idempotency_record
            WHERE tenant_id=@TenantId AND command_scope=@Scope AND idempotency_key=@Key
            """, new { TenantId = tenantId, Scope = scope, Key = key }, tx, cancellationToken: ct));
        return new(false, null, prior.RequestHash, prior.ResponseJson);
    }

    private static async Task StoreIdempotentResponseAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction tx,
        long id,
        object response,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE api_idempotency_record SET response_json=@Response::jsonb WHERE idempotency_record_id=@Id",
            new { Id = id, Response = JsonSerializer.Serialize(response) }, tx, cancellationToken: ct));
    }

    private static async Task AddActivityAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? tx, long tenantId, long caseId, string type, object detail, string actor, string traceId, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO incident_case_activity(tenant_id,case_id,activity_type,detail_json,actor_name,trace_id) VALUES(@TenantId,@CaseId,@Type,@Detail::jsonb,@Actor,@TraceId)",
            new { TenantId = tenantId, CaseId = caseId, Type = type, Detail = JsonSerializer.Serialize(detail), Actor = actor, TraceId = traceId }, tx, cancellationToken: ct));
    }

    private static string Clean(string value, int max) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Required text is missing") : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string? CleanNullable(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string NormalizePriority(string value) => value.Trim().ToLowerInvariant() is "low" or "high" or "urgent" ? value.Trim().ToLowerInvariant() : "normal";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CaseVersionRow(long CaseId, string Status, int Version);
    private sealed record IdempotencyPrior(string RequestHash, string ResponseJson);
    private sealed record IdempotencyReservation(bool Created, long? Id, string RequestHash, string? ResponseJson);
    internal sealed record CaseTemplateRow(long TemplateId,long TenantId,string TemplateCode,int Version,string Name,string? EventType,string DefaultPriority,string DefaultSlaJson,string ChecklistJson,string RequiredEvidenceJson,string Status,string CreatedBy,string? ApprovedBy,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);
}
