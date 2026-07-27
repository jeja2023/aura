using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Aura.Api.Product;
using System.Security.Cryptography;
using System.Text;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsProduct
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").WithTags("Commercial product");
        MapEvents(api);
        MapCases(api);
        MapCaseCollaboration(api);
        MapInvestigations(api);
        MapHighRiskOperations(api);
        MapHighRiskCancellation(api);
        MapOnboarding(api);
        MapGovernance(api);
        MapControlledQueries(api);
        MapOperationsCenter(api);
        MapDataLifecycle(api);
        MapLegacyMigration(api);
        MapEnterpriseIdentity(api);
        MapUsageAndEntitlements(api);
        MapReleaseGovernance(api);
        MapEvidenceExports(api);
        MapNotifications(api);
        MapRuleExecution(api);
        MapAiGovernance(api);
        MapBreakGlass(api);
        MapProductInsights(api);
    }

    private static void MapEvents(RouteGroupBuilder api)
    {
        var events = api.MapGroup("/events");
        events.MapGet("", async (
            HttpContext http,
            long tenantId,
            string? status,
            string? severity,
            string? keyword,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int page,
            int pageSize,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "event.list");
            var result = await repository.GetEventsAsync(tenantId, status, severity, keyword, from, to, page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Items, pager = new { result.Page, result.PageSize, result.Total } });
        }).RequireAuthorization("EventView");

        events.MapPost("", async (
            HttpContext http,
            BusinessEventCreateRequest request,
            EventCaseRepository repository,
            EntitlementUsageService usage,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "event.create");
            if (string.IsNullOrWhiteSpace(request.EventType) || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.AggregationKey))
                return AuraApiResults.BadRequest("事件类型、标题和聚合键不能为空", 40010);
            var entitlement = await usage.CheckAsync(new EntitlementCheckRequest(
                request.TenantId, "event_center", "business_events", 1), ct);
            if (!entitlement.Allowed)
                return AuraApiResults.Forbidden(entitlement.Reason, 40380);
            var result = await repository.CreateOrAggregateEventAsync(
                request, Actor(http), http.TraceIdentifier, IdempotencyKey(http), ct);
            if (result.Status is ProductCommandStatus.Success or ProductCommandStatus.Duplicate)
            {
                var rawKey = $"{request.AggregationKey}|{request.AggregationPolicyVersion}|{request.OccurredAt:O}";
                var usageKey = IdempotencyKey(http)
                    ?? $"event-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant()}";
                var recorded = await usage.RecordAsync(new UsageRecordRequest(
                    request.TenantId, "event_center", "business_events", 1, "event", usageKey,
                    null, null, null, request.OccurredAt, null, null), ct);
                if (recorded.Status is not (ProductCommandStatus.Success or ProductCommandStatus.Duplicate))
                    await audit.InsertOperationAsync(Actor(http), "Usage recording failed",
                        $"metric=business_events, tenantId={request.TenantId}, status={recorded.Status}, traceId={http.TraceIdentifier}");
            }
            return ToResult(result, "事件已写入");
        }).RequireAuthorization("EventManage");

        events.MapGet("/{eventId:long}", async (
            HttpContext http,
            long eventId,
            long tenantId,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"event.view:{eventId}");
            var item = await repository.GetEventAsync(tenantId, eventId, ct);
            if (item is null) return AuraApiResults.NotFound("事件不存在", 40410);
            SetEtag(http, item.Version);
            return Results.Ok(new { code = 0, msg = "查询成功", data = item });
        }).RequireAuthorization("EventView");

        events.MapPost("/{eventId:long}/acknowledge", async (
            HttpContext http,
            long eventId,
            long tenantId,
            EventActionRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) => await TransitionEventAsync(
                http, tenantId, eventId, "acknowledge", request, repository, access, audit, ct))
            .RequireAuthorization("EventManage");

        events.MapPost("/{eventId:long}/dismiss", async (
            HttpContext http,
            long eventId,
            long tenantId,
            EventActionRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) => await TransitionEventAsync(
                http, tenantId, eventId, "dismiss", request, repository, access, audit, ct))
            .RequireAuthorization("EventManage");

        events.MapPost("/{eventId:long}/reopen", async (
            HttpContext http,
            long eventId,
            long tenantId,
            EventActionRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) => await TransitionEventAsync(
                http, tenantId, eventId, "reopen", request, repository, access, audit, ct))
            .RequireAuthorization("EventManage");

        events.MapPost("/batch-triage-assign", async (
            HttpContext http,
            EventBatchTriageRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "event.batch-triage");
            if (request.EventIds.Count == 0 || request.EventIds.Count > 200)
                return AuraApiResults.BadRequest("批量事件数必须为 1-200", 40011);
            var items = await repository.BatchAssignEventsAsync(request, Actor(http), http.TraceIdentifier, ct);
            return Results.Ok(new { code = 0, msg = "批量分诊完成", data = new { items } });
        }).RequireAuthorization("EventManage");

        events.MapGet("/{eventId:long}/timeline", async (
            HttpContext http,
            long eventId,
            long tenantId,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"event.timeline:{eventId}");
            var items = await repository.GetEventTimelineAsync(tenantId, eventId, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data = items });
        }).RequireAuthorization("EventView");

        events.MapPost("/{eventId:long}/cases", async (
            HttpContext http,
            long eventId,
            CaseCreateRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"event.case-create:{eventId}");
            var key = IdempotencyKey(http);
            if (string.IsNullOrWhiteSpace(key)) return AuraApiResults.BadRequest("缺少 Idempotency-Key", 40012);
            var eventIds = (request.EventIds ?? []).Append(eventId).Distinct().ToArray();
            var result = await repository.CreateCaseAsync(request with { EventIds = eventIds }, Actor(http), http.TraceIdentifier, key, ct);
            return ToResult(result, "案件已创建");
        }).RequireAuthorization("CaseManage");
    }

    private static void MapCases(RouteGroupBuilder api)
    {
        var cases = api.MapGroup("/cases");
        cases.MapGet("", async (
            HttpContext http,
            long tenantId,
            string? status,
            string? priority,
            string? keyword,
            int page,
            int pageSize,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "case.list");
            var result = await repository.GetCasesAsync(tenantId, status, priority, keyword, page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Items, pager = new { result.Page, result.PageSize, result.Total } });
        }).RequireAuthorization("CaseView");

        cases.MapPost("", async (
            HttpContext http,
            CaseCreateRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "case.create");
            if (string.IsNullOrWhiteSpace(request.Title)) return AuraApiResults.BadRequest("案件标题不能为空", 40020);
            var key = IdempotencyKey(http);
            if (string.IsNullOrWhiteSpace(key)) return AuraApiResults.BadRequest("缺少 Idempotency-Key", 40012);
            var result = await repository.CreateCaseAsync(request, Actor(http), http.TraceIdentifier, key, ct);
            return ToResult(result, "案件已创建");
        }).RequireAuthorization("CaseManage");

        cases.MapGet("/{caseId:long}", async (
            HttpContext http,
            long caseId,
            long tenantId,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.view:{caseId}");
            var item = await repository.GetCaseAsync(tenantId, caseId, ct);
            if (item is null) return AuraApiResults.NotFound("案件不存在", 40420);
            SetEtag(http, item.Version);
            return Results.Ok(new { code = 0, msg = "查询成功", data = item });
        }).RequireAuthorization("CaseView");

        cases.MapPost("/{caseId:long}/transitions", async (
            HttpContext http,
            long caseId,
            long tenantId,
            CaseTransitionRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.transition:{caseId}");
            var canReview = AuraPermissions.HasPermission(http.User, AuraPermissions.CaseReview);
            var result = await repository.TransitionCaseAsync(
                tenantId, caseId, request, canReview, Actor(http), http.TraceIdentifier, IdempotencyKey(http), ct);
            return ToResult(result, "案件状态已更新");
        }).RequireAuthorization("CaseManage");

        cases.MapPost("/{caseId:long}/events", async (
            HttpContext http,
            long caseId,
            long tenantId,
            CaseEventLinkRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.event-link:{caseId}");
            var result = await repository.LinkCaseEventAsync(tenantId, caseId, request, Actor(http), http.TraceIdentifier, ct);
            return ToResult(result, request.Active ? "事件已关联" : "事件关联已解除");
        }).RequireAuthorization("CaseManage");

        cases.MapPost("/{caseId:long}/comments", async (
            HttpContext http,
            long caseId,
            long tenantId,
            CaseCommentRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.comment:{caseId}");
            if (string.IsNullOrWhiteSpace(request.Content)) return AuraApiResults.BadRequest("评论不能为空", 40021);
            return ToResult(await repository.AddCaseCommentAsync(tenantId, caseId, request, Actor(http), http.TraceIdentifier, ct), "评论已添加");
        }).RequireAuthorization("CaseManage");

        cases.MapPost("/{caseId:long}/evidence", async (
            HttpContext http,
            long caseId,
            long tenantId,
            CaseEvidenceRequest request,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.evidence:{caseId}");
            return ToResult(await repository.AddCaseEvidenceAsync(tenantId, caseId, request, Actor(http), http.TraceIdentifier, ct), "证据已添加");
        }).RequireAuthorization("CaseManage");

        cases.MapGet("/{caseId:long}/timeline", async (
            HttpContext http,
            long caseId,
            long tenantId,
            EventCaseRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.timeline:{caseId}");
            var items = await repository.GetCaseTimelineAsync(tenantId, caseId, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data = items });
        }).RequireAuthorization("CaseView");
    }

    private static void MapCaseCollaboration(RouteGroupBuilder api)
    {
        var cases = api.MapGroup("/cases");
        cases.MapGet("/{caseId:long}/participants", async (
            HttpContext http,long caseId,long tenantId,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.participants:{caseId}");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListParticipantsAsync(tenantId, caseId, ct) });
        }).RequireAuthorization("CaseView");

        cases.MapPost("/{caseId:long}/participants", async (
            HttpContext http,long caseId,CaseParticipantRequest request,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"case.participant.add:{caseId}");
            return ToResult(await service.AddParticipantAsync(caseId, request, Actor(http), http.TraceIdentifier, ct), "Participant added");
        }).RequireAuthorization("CaseManage");

        cases.MapDelete("/{caseId:long}/participants/{userId:long}", async (
            HttpContext http,long caseId,long userId,long tenantId,string roleType,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.participant.remove:{caseId}");
            return ToResult(await service.RemoveParticipantAsync(tenantId, caseId, userId, roleType, Actor(http), http.TraceIdentifier, ct), "Participant removed");
        }).RequireAuthorization("CaseManage");

        cases.MapGet("/{caseId:long}/checklist", async (
            HttpContext http,long caseId,long tenantId,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.checklist:{caseId}");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListChecklistAsync(tenantId, caseId, ct) });
        }).RequireAuthorization("CaseView");

        cases.MapPost("/{caseId:long}/checklist/{itemId:long}", async (
            HttpContext http,long caseId,long itemId,CaseChecklistUpdateRequest request,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"case.checklist.update:{caseId}");
            return ToResult(await service.UpdateChecklistAsync(request.TenantId, caseId, itemId, request, Actor(http), http.TraceIdentifier, ct), "Checklist updated");
        }).RequireAuthorization("CaseManage");

        cases.MapPost("/{caseId:long}/templates/{templateId:long}", async (
            HttpContext http,long caseId,long templateId,long tenantId,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"case.template.apply:{caseId}");
            return ToResult(await service.ApplyTemplateAsync(tenantId, caseId, templateId, Actor(http), http.TraceIdentifier, ct), "Template applied");
        }).RequireAuthorization("CaseManage");

        cases.MapPost("/{caseId:long}/merge", async (
            HttpContext http,long caseId,CaseMergeRequest request,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"case.merge:{caseId}");
            var key = IdempotencyKey(http);
            if (string.IsNullOrWhiteSpace(key)) return AuraApiResults.BadRequest("Missing Idempotency-Key", 40012);
            return ToResult(await service.MergeAsync(caseId, request, Actor(http), http.TraceIdentifier, key, ct), "Cases merged");
        }).RequireAuthorization("CaseReview");

        cases.MapPost("/{caseId:long}/split", async (
            HttpContext http,long caseId,CaseSplitRequest request,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"case.split:{caseId}");
            var key = IdempotencyKey(http);
            if (string.IsNullOrWhiteSpace(key)) return AuraApiResults.BadRequest("Missing Idempotency-Key", 40012);
            return ToResult(await service.SplitAsync(caseId, request, Actor(http), http.TraceIdentifier, key, ct), "Case split created");
        }).RequireAuthorization("CaseReview");

        var templates = api.MapGroup("/case-templates");
        templates.MapGet("", async (
            HttpContext http,long tenantId,CaseCollaborationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "case.templates.list");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListTemplatesAsync(tenantId, ct) });
        }).RequireAuthorization("CaseView");
        templates.MapPost("", async (
            HttpContext http,CaseTemplateWriteRequest request,CaseCollaborationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "case.template.save");
            return ToResult(await service.SaveTemplateAsync(request, Actor(http), ct), "Template saved");
        }).RequireAuthorization("CaseManage");
        templates.MapPost("/{templateId:long}/state", async (
            HttpContext http,long templateId,CaseTemplateStateRequest request,CaseCollaborationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"case.template.state:{templateId}");
            return ToResult(await service.TransitionTemplateAsync(request.TenantId, templateId, request, Actor(http), ct), "Template state updated");
        }).RequireAuthorization("CaseReview");
    }

    private static void MapInvestigations(RouteGroupBuilder api)
    {
        var investigations = api.MapGroup("/investigations");
        investigations.MapPost("", async (
            HttpContext http,
            InvestigationCreateRequest request,
            InvestigationRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "investigation.create");
            if (string.IsNullOrWhiteSpace(request.Title)) return AuraApiResults.BadRequest("调查标题不能为空", 40030);
            return Results.Ok(new { code = 0, msg = "调查已创建", data = await repository.CreateAsync(request, Actor(http), ct) });
        }).RequireAuthorization("InvestigationManage");

        investigations.MapGet("/{investigationId:long}", async (
            HttpContext http,
            long investigationId,
            long tenantId,
            InvestigationRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"investigation.view:{investigationId}");
            var data = await repository.GetAsync(tenantId, investigationId, ct);
            return data is null ? AuraApiResults.NotFound("调查不存在", 40430) : Results.Ok(new { code = 0, msg = "查询成功", data });
        }).RequireAuthorization("InvestigationView");

        investigations.MapPost("/{investigationId:long}/queries", async (
            HttpContext http,
            long investigationId,
            long tenantId,
            InvestigationQueryRequest request,
            InvestigationService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"investigation.query:{investigationId}");
            var result = await service.RunQueryAsync(tenantId, investigationId, request, Actor(http), ct);
            return ToResult(result, "调查查询完成");
        }).RequireAuthorization("InvestigationManage");

        investigations.MapGet("/{investigationId:long}/timeline", async (
            HttpContext http,
            long investigationId,
            long tenantId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit,
            InvestigationRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"investigation.timeline:{investigationId}");
            var data = await repository.GetTimelineAsync(tenantId, investigationId, from, to, limit, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data });
        }).RequireAuthorization("InvestigationView");

        investigations.MapGet("/{investigationId:long}/graph", async (
            HttpContext http,
            long investigationId,
            long tenantId,
            InvestigationRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"investigation.graph:{investigationId}");
            var data = await repository.GetEvidenceGraphAsync(tenantId, investigationId, ct);
            return data is null ? AuraApiResults.NotFound("调查不存在", 40430) : Results.Ok(new { code = 0, msg = "查询成功", data });
        }).RequireAuthorization("InvestigationView");

        investigations.MapPost("/{investigationId:long}/evidence", async (
            HttpContext http,
            long investigationId,
            long tenantId,
            InvestigationEvidenceRequest request,
            InvestigationRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"investigation.evidence:{investigationId}");
            var itemId = await repository.AddEvidenceAsync(tenantId, investigationId, request, Actor(http), ct);
            return itemId.HasValue
                ? Results.Ok(new { code = 0, msg = "证据已加入调查", data = new { itemId } })
                : AuraApiResults.BadRequest("证据哈希无效或调查不存在", 40031);
        }).RequireAuthorization("InvestigationManage");

        investigations.MapPost("/{investigationId:long}/attach-to-case", async (
            HttpContext http,
            long investigationId,
            long tenantId,
            InvestigationAttachRequest request,
            InvestigationRepository repository,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"investigation.attach:{investigationId}");
            return ToResult(await repository.AttachToCaseAsync(
                tenantId, investigationId, request, Actor(http), http.TraceIdentifier, ct), "调查证据已关联案件");
        }).RequireAuthorization("InvestigationManage");
    }

    private static void MapHighRiskOperations(RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/ops/high-risk");
        operations.MapPost("/preview", async (
            HttpContext http,
            HighRiskTaskPreviewRequest request,
            HighRiskOperationService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (request.TenantId.HasValue && !await access.CanAccessAsync(http.User, request.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId.Value, $"ops.preview:{request.OperationType}");
            if (!request.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("跨租户操作仅允许超级管理员", 40302);
            var key = IdempotencyKey(http);
            if (string.IsNullOrWhiteSpace(key)) return AuraApiResults.BadRequest("缺少 Idempotency-Key", 40012);
            return ToResult(await service.PreviewAsync(request, Actor(http), http.TraceIdentifier, key, ct), "影响预览已生成");
        }).RequireAuthorization("OpsHighImpact");

        operations.MapPost("/{taskId:long}/execute", async (
            HttpContext http,
            long taskId,
            HighRiskTaskExecuteRequest request,
            HighRiskOperationService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var task = await service.GetAsync(taskId, ct);
            if (task is null) return AuraApiResults.NotFound("高风险任务不存在", 40440);
            if (task.TenantId.HasValue && !await access.CanAccessAsync(http.User, task.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, task.TenantId.Value, $"ops.execute:{taskId}");
            if (!task.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("跨租户操作仅允许超级管理员", 40302);
            var result = await service.ExecuteAsync(taskId, request, http.User, Actor(http), http.TraceIdentifier, ct);
            return ToResult(result, "任务已进入队列");
        }).RequireAuthorization("OpsHighImpact");

        operations.MapGet("", async (
            HttpContext http,
            long? tenantId,
            string? status,
            int page,
            int pageSize,
            HighRiskOperationService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "ops.list");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("跨租户查询仅允许超级管理员", 40302);
            var result = await service.ListAsync(tenantId, status, page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Items, pager = new { result.Page, result.PageSize, result.Total } });
        }).RequireAuthorization("OpsView");

        operations.MapGet("/{taskId:long}", async (
            HttpContext http,
            long taskId,
            HighRiskOperationService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var task = await service.GetAsync(taskId, ct);
            if (task is null) return AuraApiResults.NotFound("高风险任务不存在", 40440);
            if (task.TenantId.HasValue && !await access.CanAccessAsync(http.User, task.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, task.TenantId.Value, $"ops.view:{taskId}");
            if (!task.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("跨租户查询仅允许超级管理员", 40302);
            SetEtag(http, task.Version);
            return Results.Ok(new { code = 0, msg = "查询成功", data = task });
        }).RequireAuthorization("OpsView");
    }

    private static void MapHighRiskCancellation(RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/ops/high-risk");
        operations.MapPost("/{taskId:long}/cancel", async (
            HttpContext http,
            long taskId,
            HighRiskTaskCancelRequest request,
            HighRiskOperationService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var task = await service.GetAsync(taskId, ct);
            if (task is null) return AuraApiResults.NotFound("High-risk task not found", 40440);
            if (task.TenantId.HasValue && !await access.CanAccessAsync(http.User, task.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, task.TenantId.Value, $"ops.cancel:{taskId}");
            if (!task.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Only a global administrator can cancel a cross-tenant task", 40302);
            return ToResult(await service.CancelAsync(taskId, request.ExpectedVersion, Actor(http), http.TraceIdentifier, ct), "Task cancelled");
        }).RequireAuthorization("OpsHighImpact");
    }

    private static void MapOnboarding(RouteGroupBuilder api)
    {
        var onboarding = api.MapGroup("/integrations/onboarding");
        onboarding.MapGet("", async (
            HttpContext http,long tenantId,int page,int pageSize,IntegrationOnboardingService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "integration.onboarding.list");
            var result = await service.ListAsync(tenantId, page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Items, pager = new { result.Page, result.PageSize, result.Total } });
        }).RequireAuthorization("IntegrationView");

        onboarding.MapPost("", async (
            HttpContext http,OnboardingCreateRequest request,IntegrationOnboardingService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "integration.onboarding.create");
            try
            {
                return Results.Ok(new { code = 0, msg = "接入向导已创建", data = await service.CreateAsync(request, Actor(http), ct) });
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40050);
            }
        }).RequireAuthorization("IntegrationManage");

        onboarding.MapGet("/{onboardingId:long}", async (
            HttpContext http,long onboardingId,long tenantId,IntegrationOnboardingService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"integration.onboarding.view:{onboardingId}");
            var data = await service.GetAsync(tenantId, onboardingId, ct);
            return data is null ? AuraApiResults.NotFound("接入向导不存在", 40450) : Results.Ok(new { code = 0, msg = "查询成功", data });
        }).RequireAuthorization("IntegrationView");

        onboarding.MapPost("/{onboardingId:long}/steps", async (
            HttpContext http,long onboardingId,long tenantId,OnboardingStepRequest request,IntegrationOnboardingService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"integration.onboarding.step:{onboardingId}");
            return ToResult(await service.ApplyStepAsync(tenantId, onboardingId, request, Actor(http), ct), "向导步骤已保存");
        }).RequireAuthorization("IntegrationTest");

        onboarding.MapPost("/{onboardingId:long}/rollback", async (
            HttpContext http,long onboardingId,long tenantId,OnboardingRollbackRequest request,IntegrationOnboardingService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"integration.onboarding.rollback:{onboardingId}");
            return ToResult(await service.RollbackAsync(tenantId, onboardingId, request.TargetVersion, Actor(http), ct), "配置已回滚");
        }).RequireAuthorization("IntegrationManage");

        onboarding.MapGet("/{onboardingId:long}/export", async (
            HttpContext http,long onboardingId,long tenantId,IntegrationOnboardingService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"integration.onboarding.export:{onboardingId}");
            var data = await service.ExportAsync(tenantId, onboardingId, ct);
            return data is null ? AuraApiResults.NotFound("接入向导不存在", 40450) : Results.Ok(new { code = 0, msg = "导出成功", data });
        }).RequireAuthorization("IntegrationView");
    }

    private static void MapGovernance(RouteGroupBuilder api)
    {
        var governance = api.MapGroup("/governance");

        governance.MapGet("/{resource}", async (
            HttpContext http,
            string resource,
            long? tenantId,
            int limit,
            GovernanceCatalogService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var descriptor = GovernanceCatalogService.Describe(resource);
            if (descriptor is null) return AuraApiResults.NotFound("Unknown governance resource", 40460);
            if (!AuraPermissions.HasPermission(http.User, descriptor.ViewPermission))
                return AuraApiResults.Forbidden("Missing governance view permission", 40360);
            var scopeError = await GovernanceScopeErrorAsync(http, descriptor, tenantId, access, audit, resource, ct);
            if (scopeError is not null) return scopeError;
            var data = await service.ListAsync(resource, tenantId, limit == 0 ? 100 : limit, ct);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data });
        }).RequireAuthorization();

        governance.MapPost("/{resource}", async (
            HttpContext http,
            string resource,
            GovernanceWriteRequest request,
            GovernanceCatalogService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var descriptor = GovernanceCatalogService.Describe(resource);
            if (descriptor is null) return AuraApiResults.NotFound("Unknown governance resource", 40460);
            if (!AuraPermissions.HasPermission(http.User, descriptor.ManagePermission))
                return AuraApiResults.Forbidden("Missing governance manage permission", 40360);
            var scopeError = await GovernanceScopeErrorAsync(http, descriptor, request.TenantId, access, audit, resource, ct);
            if (scopeError is not null) return scopeError;
            var result = await service.CreateAsync(resource, request.TenantId, request.Payload, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Governance resource created", $"resource={resource}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Governance resource created");
        }).RequireAuthorization();

        governance.MapPost("/{resource}/{id:long}/transitions", async (
            HttpContext http,
            string resource,
            long id,
            GovernanceTransitionRequest request,
            GovernanceCatalogService service,
            TenantScopeAccessService access,
            StepUpAuthorizationService stepUp,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var descriptor = GovernanceCatalogService.Describe(resource);
            if (descriptor is null) return AuraApiResults.NotFound("Unknown governance resource", 40460);
            if (!AuraPermissions.HasPermission(http.User, descriptor.ManagePermission))
                return AuraApiResults.Forbidden("Missing governance manage permission", 40360);
            var scopeError = await GovernanceScopeErrorAsync(http, descriptor, request.TenantId, access, audit, resource, ct);
            if (scopeError is not null) return scopeError;
            if (RequiresGovernanceStepUp(resource, request.TargetStatus) && !stepUp.HasRecentStepUp(http.User))
                return AuraApiResults.Forbidden("This governance transition requires recent MFA or step-up", 40362);
            var approvalPermission = resource.Equals("rules", StringComparison.OrdinalIgnoreCase)
                ? AuraPermissions.RuleApprove
                : resource.Equals("ai-models", StringComparison.OrdinalIgnoreCase)
                    ? AuraPermissions.AiReleaseApprove
                    : resource.Equals("legal-holds", StringComparison.OrdinalIgnoreCase)
                        ? AuraPermissions.EvidenceLegalHold
                        : descriptor.ManagePermission;
            var canApprove = AuraPermissions.HasPermission(http.User, approvalPermission);
            try
            {
                var result = await service.TransitionAsync(resource, id, request.TenantId, request.TargetStatus, request.Reason, canApprove, Actor(http), ct);
                if (result.Status == ProductCommandStatus.Success)
                    await audit.InsertOperationAsync(Actor(http), "Governance status changed", $"resource={resource}, id={id}, target={request.TargetStatus}, traceId={http.TraceIdentifier}");
                return ToResult(result, "Governance status changed");
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40060);
            }
        }).RequireAuthorization();

        api.MapPost("/rules/{ruleId:long}/dry-run", async (
            HttpContext http,
            long ruleId,
            RuleDryRunRequest request,
            GovernanceCatalogService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"rule.dry-run:{ruleId}");
            var data = await service.DryRunRuleAsync(ruleId, request, ct);
            if (data is null) return AuraApiResults.NotFound("Rule version not found", 40461);
            await audit.InsertOperationAsync(Actor(http), "Rule dry-run", $"ruleId={ruleId}, version={request.Version}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
            return Results.Ok(new { code = 0, msg = "Rule dry-run completed", data });
        }).RequireAuthorization("RuleManage");
    }

    private static async Task<IResult?> GovernanceScopeErrorAsync(
        HttpContext http,
        GovernanceCatalogService.ResourceDescriptor descriptor,
        long? tenantId,
        TenantScopeAccessService access,
        AuditRepository audit,
        string resource,
        CancellationToken ct)
    {
        if (!descriptor.TenantScoped) return null;
        if (tenantId.HasValue)
            return await access.CanAccessAsync(http.User, tenantId.Value, ct)
                ? null
                : await TenantForbiddenAsync(http, audit, tenantId.Value, $"governance:{resource}");
        return TenantScopeAccessService.IsSuperAdmin(http.User)
            ? null
            : AuraApiResults.Forbidden("tenantId is required for tenant-scoped governance resources", 40361);
    }

    internal static bool RequiresGovernanceStepUp(string resource, string targetStatus)
    {
        var normalizedResource = resource.Trim().ToLowerInvariant();
        var target = targetStatus.Trim().ToLowerInvariant();
        return normalizedResource switch
        {
            "rules" => target is "canary" or "published",
            "ai-models" => target is "approved" or "canary" or "production" or "rolled_back",
            "legal-holds" => target == "released",
            "retention-policies" => target == "active",
            _ => false
        };
    }

    private static void MapControlledQueries(RouteGroupBuilder api)
    {
        var queries = api.MapGroup("/controlled-queries");
        queries.MapPost("", async (
            HttpContext http,
            ControlledQueryRequest request,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "controlled-query.plan");
            var result = await service.CreatePlanAsync(request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Controlled query planned", $"tenantId={request.TenantId}, investigationId={request.InvestigationId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Controlled query plan created");
        }).RequireAuthorization("InvestigationManage");

        queries.MapGet("/{queryPlanId:long}", async (
            HttpContext http,
            long queryPlanId,
            long tenantId,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"controlled-query.view:{queryPlanId}");
            var data = await service.GetAsync(tenantId, queryPlanId, ct);
            return data is null
                ? AuraApiResults.NotFound("Controlled query plan not found", 40462)
                : Results.Ok(new { code = 0, msg = "Query succeeded", data });
        }).RequireAuthorization("InvestigationView");

        queries.MapPut("/{queryPlanId:long}/plan", async (
            HttpContext http,
            long queryPlanId,
            ControlledQueryPlanUpdateRequest request,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"controlled-query.plan.update:{queryPlanId}");
            var result = await service.UpdatePendingPlanAsync(queryPlanId, request, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Controlled query plan updated", $"queryPlanId={queryPlanId}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Controlled query plan updated");
        }).RequireAuthorization("InvestigationManage");

        queries.MapPost("/{queryPlanId:long}/confirm", async (
            HttpContext http,
            long queryPlanId,
            ControlledQueryConfirmRequest request,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"controlled-query.confirm:{queryPlanId}");
            var result = await service.ConfirmAsync(queryPlanId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), request.Confirm ? "Controlled query confirmed" : "Controlled query rejected", $"queryPlanId={queryPlanId}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, request.Confirm ? "Controlled query confirmed" : "Controlled query rejected");
        }).RequireAuthorization("InvestigationManage");

        queries.MapPost("/{queryPlanId:long}/execute", async (
            HttpContext http,
            long queryPlanId,
            long tenantId,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"controlled-query.execute:{queryPlanId}");
            var result = await service.ExecuteAsync(tenantId, queryPlanId, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Controlled query executed", $"queryPlanId={queryPlanId}, tenantId={tenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Controlled query executed");
        }).RequireAuthorization("InvestigationManage");

        queries.MapPost("/safety-evaluations", async (
            HttpContext http,
            long? tenantId,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "controlled-query.safety.evaluate");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global safety evaluation requires a global administrator", 40302);
            var result = await service.RunSafetyEvaluationAsync(tenantId, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Controlled query safety evaluated", $"tenantId={tenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Controlled query safety evaluation completed");
        }).RequireAuthorization("AiGovernanceManage");

        queries.MapGet("/safety-evaluations", async (
            HttpContext http,
            long? tenantId,
            int? limit,
            ControlledQueryService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "controlled-query.safety.list");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global safety evaluation history requires a global administrator", 40302);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListSafetyEvaluationsAsync(tenantId, limit ?? 50, ct) });
        }).RequireAuthorization("AiGovernanceView");
    }

    private static void MapOperationsCenter(RouteGroupBuilder api)
    {
        var operations = api.MapGroup("/ops");
        operations.MapGet("/center", async (
            HttpContext http,
            long? tenantId,
            OperationsCenterService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "ops.center");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global operations center requires a global administrator", 40302);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.GetAsync(tenantId, ct) });
        }).RequireAuthorization("OpsView");

        operations.MapPost("/slo/{policyId:long}/calculate", async (
            HttpContext http,
            long policyId,
            long? tenantId,
            OperationsCenterService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, $"ops.slo.calculate:{policyId}");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global SLO calculation requires a global administrator", 40302);
            var result = await service.CalculateSloAsync(policyId, tenantId, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "SLO snapshot calculated", $"policyId={policyId}, tenantId={tenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "SLO snapshot calculated");
        }).RequireAuthorization("OpsExecute");
    }

    private static void MapDataLifecycle(RouteGroupBuilder api)
    {
        var lifecycle = api.MapGroup("/data-lifecycle/jobs");
        lifecycle.MapPost("", async (
            HttpContext http,
            CleanupJobCreateRequest request,
            DataLifecycleService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (request.TenantId.HasValue && !await access.CanAccessAsync(http.User, request.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId.Value, "data-lifecycle.create");
            if (!request.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global cleanup requires a global administrator", 40302);
            var result = await service.CreateAsync(request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), request.DryRun ? "Cleanup dry-run queued" : "Cleanup queued", $"policyId={request.PolicyId}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Cleanup job queued");
        }).RequireAuthorization("DataGovernanceManage");

        lifecycle.MapGet("", async (
            HttpContext http,
            long? tenantId,
            int page,
            int pageSize,
            DataLifecycleService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "data-lifecycle.list");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global cleanup history requires a global administrator", 40302);
            var result = await service.ListAsync(tenantId, page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = result.Items, pager = new { result.Page, result.PageSize, result.Total } });
        }).RequireAuthorization("DataGovernanceView");

        lifecycle.MapGet("/{cleanupJobId:long}", async (
            HttpContext http,
            long cleanupJobId,
            DataLifecycleService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var job = await service.GetAsync(cleanupJobId, ct);
            if (job is null) return AuraApiResults.NotFound("Cleanup job not found", 40463);
            if (job.TenantId.HasValue && !await access.CanAccessAsync(http.User, job.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, job.TenantId.Value, $"data-lifecycle.view:{cleanupJobId}");
            if (!job.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global cleanup history requires a global administrator", 40302);
            SetEtag(http, job.Version);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = job });
        }).RequireAuthorization("DataGovernanceView");

        lifecycle.MapPost("/{cleanupJobId:long}/cancel", async (
            HttpContext http,
            long cleanupJobId,
            CleanupJobCancelRequest request,
            DataLifecycleService service,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var job = await service.GetAsync(cleanupJobId, ct);
            if (job is null) return AuraApiResults.NotFound("Cleanup job not found", 40463);
            if (job.TenantId.HasValue && !await access.CanAccessAsync(http.User, job.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, job.TenantId.Value, $"data-lifecycle.cancel:{cleanupJobId}");
            if (!job.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global cleanup requires a global administrator", 40302);
            var result = await service.CancelAsync(cleanupJobId, request.ExpectedVersion, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Cleanup cancelled", $"cleanupJobId={cleanupJobId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Cleanup job cancelled");
        }).RequireAuthorization("DataGovernanceManage");

        lifecycle.MapGet("/{cleanupJobId:long}/deletion-deliveries", async (
            HttpContext http,
            long cleanupJobId,
            DataLifecycleService lifecycleService,
            DataDeletionProjectionService deletionService,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var job = await lifecycleService.GetAsync(cleanupJobId, ct);
            if (job is null) return AuraApiResults.NotFound("Cleanup job not found", 40463);
            if (job.TenantId.HasValue && !await access.CanAccessAsync(http.User, job.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, job.TenantId.Value, $"data-lifecycle.deliveries:{cleanupJobId}");
            if (!job.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global cleanup history requires a global administrator", 40302);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await deletionService.ListAsync(cleanupJobId, ct) });
        }).RequireAuthorization("DataGovernanceView");

        lifecycle.MapPost("/{cleanupJobId:long}/deletion-deliveries/replay", async (
            HttpContext http,
            long cleanupJobId,
            string? storeType,
            DataLifecycleService lifecycleService,
            DataDeletionProjectionService deletionService,
            TenantScopeAccessService access,
            AuditRepository audit,
            CancellationToken ct) =>
        {
            var job = await lifecycleService.GetAsync(cleanupJobId, ct);
            if (job is null) return AuraApiResults.NotFound("Cleanup job not found", 40463);
            if (job.TenantId.HasValue && !await access.CanAccessAsync(http.User, job.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, job.TenantId.Value, $"data-lifecycle.deliveries.replay:{cleanupJobId}");
            if (!job.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global cleanup requires a global administrator", 40302);
            var result = await deletionService.ReplayAsync(cleanupJobId, storeType, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Deletion delivery replay queued", $"cleanupJobId={cleanupJobId}, storeType={storeType}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Deletion delivery replay queued");
        }).RequireAuthorization("DataGovernanceManage");
    }

    private static void MapLegacyMigration(RouteGroupBuilder api)
    {
        var migration = api.MapGroup("/migrations/legacy-cases");
        migration.MapGet("/preflight", async (
            HttpContext http,long? tenantId,LegacyCaseMigrationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "legacy-migration.preflight");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global migration preflight requires a global administrator", 40302);
            return Results.Ok(new { code = 0, msg = "Preflight completed", data = await service.PreflightAsync(tenantId, ct) });
        }).RequireAuthorization("OpsHighImpact");

        migration.MapPost("", async (
            HttpContext http,LegacyMigrationStartRequest request,LegacyCaseMigrationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (request.TenantId.HasValue && !await access.CanAccessAsync(http.User, request.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId.Value, "legacy-migration.start");
            if (!request.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global migration requires a global administrator", 40302);
            try
            {
                var result = await service.StartAsync(request, Actor(http), ct);
                if (result.Status == ProductCommandStatus.Success)
                    await audit.InsertOperationAsync(Actor(http), "Legacy migration started", $"batch={request.BatchName}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
                return ToResult(result, "Migration run created");
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40064);
            }
        }).RequireAuthorization("OpsHighImpact");

        migration.MapGet("/{runId:long}", async (
            HttpContext http,long runId,LegacyCaseMigrationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            var run = await service.GetAsync(runId, ct);
            if (run is null) return AuraApiResults.NotFound("Migration run not found", 40464);
            if (run.TenantId.HasValue && !await access.CanAccessAsync(http.User, run.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, run.TenantId.Value, $"legacy-migration.view:{runId}");
            if (!run.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global migration requires a global administrator", 40302);
            SetEtag(http, run.Version);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = run });
        }).RequireAuthorization("OpsView");

        migration.MapPost("/{runId:long}/backfill", async (
            HttpContext http,long runId,LegacyMigrationBackfillRequest request,LegacyCaseMigrationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            var run = await service.GetAsync(runId, ct);
            if (run is null) return AuraApiResults.NotFound("Migration run not found", 40464);
            if (run.TenantId.HasValue && !await access.CanAccessAsync(http.User, run.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, run.TenantId.Value, $"legacy-migration.backfill:{runId}");
            if (!run.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global migration requires a global administrator", 40302);
            var result = await service.BackfillAsync(runId, request, Actor(http), ct);
            await audit.InsertOperationAsync(Actor(http), "Legacy migration backfill", $"runId={runId}, batchSize={request.BatchSize}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Migration batch processed");
        }).RequireAuthorization("OpsHighImpact");

        migration.MapGet("/{runId:long}/reconciliation", async (
            HttpContext http,long runId,LegacyCaseMigrationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            var run = await service.GetAsync(runId, ct);
            if (run is null) return AuraApiResults.NotFound("Migration run not found", 40464);
            if (run.TenantId.HasValue && !await access.CanAccessAsync(http.User, run.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, run.TenantId.Value, $"legacy-migration.reconcile:{runId}");
            if (!run.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global migration requires a global administrator", 40302);
            return Results.Ok(new { code = 0, msg = "Reconciliation completed", data = await service.ReconcileAsync(runId, ct) });
        }).RequireAuthorization("OpsView");

        migration.MapPost("/{runId:long}/cutover", async (
            HttpContext http,long runId,LegacyMigrationCutoverRequest request,LegacyCaseMigrationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            var run = await service.GetAsync(runId, ct);
            if (run is null) return AuraApiResults.NotFound("Migration run not found", 40464);
            if (run.TenantId.HasValue && !await access.CanAccessAsync(http.User, run.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, run.TenantId.Value, $"legacy-migration.cutover:{runId}");
            if (!run.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global migration requires a global administrator", 40302);
            var result = await service.CutoverAsync(runId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Legacy migration read mode changed", $"runId={runId}, mode={request.TargetReadMode}, approval={request.ApprovalReference}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Migration read mode changed");
        }).RequireAuthorization("OpsHighImpact");
    }

    private static void MapEnterpriseIdentity(RouteGroupBuilder api)
    {
        var identity = api.MapGroup("/identity");
        identity.MapGet("/oidc/providers", async (
            HttpContext http,long tenantId,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "identity.oidc.providers");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListProvidersAsync(tenantId, ct) });
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/oidc/providers", async (
            HttpContext http,OidcProviderWriteRequest request,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "identity.oidc.provider.create");
            try
            {
                var result = await service.CreateProviderAsync(request, Actor(http), ct);
                if (result.Status == ProductCommandStatus.Success)
                    await audit.InsertOperationAsync(Actor(http), "OIDC provider version created", $"tenantId={request.TenantId}, provider={request.ProviderCode}, traceId={http.TraceIdentifier}");
                return ToResult(result, "OIDC provider created");
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40070);
            }
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/oidc/providers/{providerId:long}/validate", async (
            HttpContext http,long providerId,long tenantId,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"identity.oidc.validate:{providerId}");
            return ToResult(await service.ValidateProviderAsync(tenantId, providerId, ct), "OIDC provider validated");
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/oidc/providers/{providerId:long}/enabled", async (
            HttpContext http,long providerId,OidcProviderTransitionRequest request,IdentityFederationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"identity.oidc.enabled:{providerId}");
            var result = await service.SetProviderEnabledAsync(providerId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "OIDC provider state changed", $"providerId={providerId}, enabled={request.Enabled}, traceId={http.TraceIdentifier}");
            return ToResult(result, "OIDC provider state changed");
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/oidc/mappings", async (
            HttpContext http,IdentityGroupMappingWriteRequest request,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "identity.mapping.create");
            return ToResult(await service.CreateMappingAsync(request, ct), "Group mapping created as draft");
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/oidc/mappings/{mappingId:long}/approve", async (
            HttpContext http,long mappingId,long tenantId,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"identity.mapping.approve:{mappingId}");
            var result = await service.ApproveMappingAsync(tenantId, mappingId, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "OIDC group mapping approved", $"mappingId={mappingId}, tenantId={tenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Group mapping approved");
        }).RequireAuthorization("OpsHighImpact");

        identity.MapPost("/oidc/mappings/preview", async (
            HttpContext http,IdentityGroupPreviewRequest request,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "identity.mapping.preview");
            return Results.Ok(new { code = 0, msg = "Preview succeeded", data = await service.PreviewMappingsAsync(request, ct) });
        }).RequireAuthorization("TenantManage");

        identity.MapGet("/oidc/{tenantId:long}/{providerCode}/authorize", (
            HttpContext http,long tenantId,string providerCode,string? returnUrl,Guid? stepUpChallengeId,
            IdentityFederationService service,CancellationToken ct) =>
            service.BeginAuthorizationAsync(http, tenantId, providerCode, returnUrl, stepUpChallengeId, ct));

        identity.MapGet("/oidc/callback", (
            HttpContext http,string? code,string? state,string? error,IdentityFederationService service,
            IdentityAdminService identityService,CancellationToken ct) =>
            service.CompleteAuthorizationAsync(http, code, state, error, identityService, ct));

        identity.MapGet("/sessions", async (
            HttpContext http,long? tenantId,string? userName,IdentityFederationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "identity.sessions");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global session inventory requires a global administrator", 40302);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListSessionsAsync(tenantId, userName, ct) });
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/sessions/{sessionId:guid}/revoke", async (
            HttpContext http,Guid sessionId,AuthSessionRevokeRequest request,IdentityFederationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            var session = await service.GetSessionAsync(sessionId, ct);
            if (session is null) return AuraApiResults.NotFound("Session not found", 40471);
            if (session.TenantId.HasValue && !await access.CanAccessAsync(http.User, session.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, session.TenantId.Value, $"identity.session.revoke:{sessionId}");
            if (!session.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("Global session revocation requires a global administrator", 40302);
            var result = await service.RevokeSessionAsync(sessionId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Session revoked", $"sessionId={sessionId}, reason={request.Reason}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Session revoked");
        }).RequireAuthorization("TenantManage");

        identity.MapPost("/step-up", async (
            HttpContext http,StepUpChallengeRequest request,IdentityFederationService service,AuditRepository audit,CancellationToken ct) =>
        {
            var result = await service.CreateStepUpAsync(http.User, request, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Step-up challenge created", $"action={request.Action}, resource={request.ResourceRef}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Step-up challenge created");
        }).RequireAuthorization();
    }

    private static void MapUsageAndEntitlements(RouteGroupBuilder api)
    {
        var usage = api.MapGroup("/commercial");
        usage.MapPost("/entitlements/check", async (
            HttpContext http,EntitlementCheckRequest request,EntitlementUsageService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "entitlement.check");
            return Results.Ok(new { code = 0, msg = "Entitlement evaluated", data = await service.CheckAsync(request, ct) });
        }).RequireAuthorization("UsageView");

        usage.MapPost("/usage", async (
            HttpContext http,UsageRecordRequest request,EntitlementUsageService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "usage.record");
            var result = await service.RecordAsync(request, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Tenant usage recorded", $"tenantId={request.TenantId}, metric={request.MetricCode}, quantity={request.Quantity}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Usage recorded");
        }).RequireAuthorization("UsageManage");

        usage.MapGet("/usage/report", async (
            HttpContext http,long tenantId,DateTimeOffset? from,DateTimeOffset? to,EntitlementUsageService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "usage.report");
            try
            {
                return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.GetReportAsync(tenantId, from, to, ct) });
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40080);
            }
        }).RequireAuthorization("UsageView");
    }

    private static void MapReleaseGovernance(RouteGroupBuilder api)
    {
        var release = api.MapGroup("/release-governance");
        release.MapGet("/profiles", async (int page,int pageSize,ReleaseGovernanceService service,CancellationToken ct) =>
        {
            var result = await service.ListProfilesAsync(page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = result.Items, pager = new { result.Page, result.PageSize, result.Total } });
        }).RequireAuthorization("OpsView");

        release.MapPost("/profiles", async (
            HttpContext http,ServiceProfileCreateRequest request,ReleaseGovernanceService service,AuditRepository audit,CancellationToken ct) =>
        {
            try
            {
                var result = await service.CreateProfileAsync(request, Actor(http), ct);
                if (result.Status == ProductCommandStatus.Success)
                    await audit.InsertOperationAsync(Actor(http), "Service profile draft created", $"profile={request.ProfileCode}, deliveryMode={request.DeliveryMode}, traceId={http.TraceIdentifier}");
                return ToResult(result, "Service profile draft created");
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40090);
            }
        }).RequireAuthorization("OpsExecute");

        release.MapGet("/profiles/{profileId:long}", async (long profileId,ReleaseGovernanceService service,HttpContext http,CancellationToken ct) =>
        {
            var data = await service.GetProfileAsync(profileId, ct);
            if (data is null) return AuraApiResults.NotFound("Service profile not found", 40490);
            SetEtag(http, data.Version);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data });
        }).RequireAuthorization("OpsView");

        release.MapPost("/profiles/{profileId:long}/approve", async (
            HttpContext http,long profileId,ServiceProfileApproveRequest request,ReleaseGovernanceService service,
            StepUpAuthorizationService stepUp,AuditRepository audit,CancellationToken ct) =>
        {
            if (!stepUp.HasRecentStepUp(http.User))
                return AuraApiResults.Forbidden("Service profile approval requires recent MFA or step-up", 40390);
            var result = await service.ApproveProfileAsync(profileId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Service profile approved", $"profileId={profileId}, approval={request.ApprovalReference}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Service profile approved");
        }).RequireAuthorization("OpsHighImpact");

        release.MapPost("/gates", async (
            HttpContext http,ReleaseGateStartRequest request,ReleaseGovernanceService service,AuditRepository audit,CancellationToken ct) =>
        {
            var result = await service.StartGateAsync(request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Commercial release gate started", $"profileId={request.ServiceProfileId}, build={request.BuildVersion}, commit={request.GitCommit}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Release gate started");
        }).RequireAuthorization("OpsHighImpact");

        release.MapPost("/gates/{gateRunId:long}/evidence", async (
            HttpContext http,long gateRunId,ReleaseGateEvidenceRequest request,ReleaseGovernanceService service,
            AuditRepository audit,CancellationToken ct) =>
        {
            var result = await service.SubmitEvidenceAsync(gateRunId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Release gate evidence submitted", $"gateRunId={gateRunId}, check={request.CheckCode}, status={request.Status}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Release gate evidence submitted");
        }).RequireAuthorization("OpsHighImpact");

        release.MapPost("/gates/{gateRunId:long}/complete", async (
            HttpContext http,long gateRunId,ReleaseGateCompleteRequest request,ReleaseGovernanceService service,
            StepUpAuthorizationService stepUp,AuditRepository audit,CancellationToken ct) =>
        {
            if (!stepUp.HasRecentStepUp(http.User))
                return AuraApiResults.Forbidden("Release gate completion requires recent MFA or step-up", 40390);
            var result = await service.CompleteGateAsync(gateRunId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Commercial release gate completed", $"gateRunId={gateRunId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Release gate completed");
        }).RequireAuthorization("OpsHighImpact");

        release.MapGet("/gates/{gateRunId:long}", async (long gateRunId,ReleaseGovernanceService service,CancellationToken ct) =>
        {
            var data = await service.GetGateAsync(gateRunId, ct);
            return data is null ? AuraApiResults.NotFound("Release gate not found", 40491) : Results.Ok(new { code = 0, msg = "Query succeeded", data });
        }).RequireAuthorization("OpsView");

        release.MapGet("/capabilities", async (string? productVersion,ReleaseGovernanceService service,CancellationToken ct) =>
            Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListCapabilitiesAsync(productVersion, ct) }))
            .RequireAuthorization("IntegrationView");
    }

    private static void MapEvidenceExports(RouteGroupBuilder api)
    {
        var exports = api.MapGroup("/evidence-exports");
        api.MapPost("/cases/{caseId:long}/evidence-exports", async (
            HttpContext http,long caseId,EvidenceExportCreateRequest request,EvidenceExportService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"evidence.export.create:{caseId}");
            var canOriginal = AuraPermissions.HasPermission(http.User, AuraPermissions.EvidenceViewOriginal);
            var result = await service.CreateAsync(caseId, request, canOriginal, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Evidence export generated", $"caseId={caseId}, tenantId={request.TenantId}, masking={request.MaskingPolicy}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Evidence export generated");
        }).RequireAuthorization("EvidenceExport");

        exports.MapPost("/{exportId:long}/grants", async (
            HttpContext http,long exportId,long tenantId,EvidenceAccessGrantRequest request,EvidenceExportService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"evidence.export.grant:{exportId}");
            var result = await service.CreateGrantAsync(tenantId, exportId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Evidence download grant created", $"exportId={exportId}, tenantId={tenantId}, grantee={request.Grantee}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Evidence access grant created");
        }).RequireAuthorization("EvidenceExport");

        exports.MapGet("/{exportId:long}/download", async (
            HttpContext http,long exportId,long tenantId,string? token,EvidenceExportService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"evidence.export.download:{exportId}");
            var canOriginal = AuraPermissions.HasPermission(http.User, AuraPermissions.EvidenceViewOriginal);
            var download = await service.AuthorizeDownloadAsync(tenantId, exportId, token, Actor(http), canOriginal, ct);
            if (download is null) return AuraApiResults.Forbidden("Evidence export is unavailable, expired, revoked, or the grant is invalid", 40392);
            await audit.InsertOperationAsync(Actor(http), "Evidence export downloaded", $"exportId={exportId}, tenantId={tenantId}, manifestSha256={download.ManifestSha256}, traceId={http.TraceIdentifier}");
            http.Response.Headers["X-Evidence-Manifest-SHA256"] = download.ManifestSha256;
            return Results.File(download.Path, "application/zip", download.FileName, enableRangeProcessing: false);
        }).RequireAuthorization("EvidenceExport");
    }

    private static void MapNotifications(RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/notifications");
        notifications.MapGet("/channels", async (
            HttpContext http,long? tenantId,NotificationOrchestrationService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (tenantId.HasValue && !await access.CanAccessAsync(http.User, tenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, tenantId.Value, "notification.channels.list");
            if (!tenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User)) return AuraApiResults.Forbidden("Global notification channels require super admin", 40390);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListChannelConfigsAsync(tenantId, ct) });
        }).RequireAuthorization("IntegrationManage");

        notifications.MapPost("/channels", async (
            HttpContext http,NotificationChannelConfigWriteRequest request,NotificationOrchestrationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (request.TenantId.HasValue && !await access.CanAccessAsync(http.User, request.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId.Value, "notification.channels.save");
            if (!request.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User)) return AuraApiResults.Forbidden("Global notification channels require super admin", 40390);
            return ToResult(await service.SaveChannelConfigAsync(request, Actor(http), ct), "Notification channel saved");
        }).RequireAuthorization("IntegrationManage");

        notifications.MapPost("/channels/{channelConfigId:long}/state", async (
            HttpContext http,long channelConfigId,NotificationChannelConfigStateRequest request,NotificationOrchestrationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (request.TenantId.HasValue && !await access.CanAccessAsync(http.User, request.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId.Value, $"notification.channel.state:{channelConfigId}");
            if (!request.TenantId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User)) return AuraApiResults.Forbidden("Global notification channels require super admin", 40390);
            return ToResult(await service.TransitionChannelConfigAsync(channelConfigId, request, ct), "Notification channel state updated");
        }).RequireAuthorization("IntegrationManage");

        notifications.MapPost("", async (
            HttpContext http,NotificationSendRequest request,NotificationOrchestrationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "notification.queue");
            var result = await service.QueueAsync(request, Actor(http), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Notification queued", $"tenantId={request.TenantId}, channel={request.Channel}, caseId={request.CaseId}, eventId={request.EventId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Notification queued");
        }).RequireAuthorization("CaseManage");

        notifications.MapPost("/{notificationId:long}/receipts", async (
            HttpContext http,long notificationId,NotificationReceiptRequest request,NotificationOrchestrationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"notification.receipt:{notificationId}");
            var result = await service.ApplyReceiptAsync(notificationId, request, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Notification receipt applied", $"notificationId={notificationId}, providerReceipt={request.ProviderReceiptId}, status={request.Status}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Notification receipt applied");
        }).RequireAuthorization("IntegrationManage");
    }

    private static void MapRuleExecution(RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/rules");
        rules.MapPost("/{ruleId:long}/evaluate", async (
            HttpContext http,long ruleId,RuleEvaluateRequest request,RuleAutomationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"rule.evaluate:{ruleId}");
            var data = await service.EvaluateEventAsync(request.TenantId, request.EventId, ct);
            return Results.Ok(new { code = 0, msg = "Rule evaluation completed", data });
        }).RequireAuthorization("RuleManage");
        rules.MapGet("/executions", async (
            HttpContext http,long tenantId,long? ruleId,int page,int pageSize,RuleAutomationService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "rule.executions.list");
            var result = await service.GetExecutionsAsync(tenantId, ruleId, page, pageSize, ct);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = result.Items, pager = new { result.Page,result.PageSize,result.Total } });
        }).RequireAuthorization("RuleView");
        rules.MapPost("/{ruleId:long}/rollback", async (
            HttpContext http,long ruleId,RuleRollbackRequest request,RuleAutomationService service,StepUpAuthorizationService stepUp,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"rule.rollback:{ruleId}");
            if (!stepUp.HasRecentStepUp(http.User)) return AuraApiResults.Forbidden("Rule rollback requires recent MFA or step-up", 40394);
            var result = await service.RollbackAsync(ruleId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Rule rolled back", $"ruleId={ruleId}, version={request.TargetVersion}, reason={request.Reason}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Rule rolled back");
        }).RequireAuthorization("RuleApprove");
    }

    private static void MapAiGovernance(RouteGroupBuilder api)
    {
        var ai = api.MapGroup("/ai-governance");
        ai.MapPost("/evaluations/{evaluationRunId:long}/complete", async (
            HttpContext http,long evaluationRunId,AiEvaluationCompleteRequest request,AiGovernanceService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (request.TenantId.HasValue && !await access.CanAccessAsync(http.User, request.TenantId.Value, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId.Value, $"ai.evaluation.complete:{evaluationRunId}");
            var result = await service.CompleteEvaluationAsync(evaluationRunId, request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "AI evaluation completed", $"evaluationRunId={evaluationRunId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "AI evaluation completed");
        }).RequireAuthorization("AiGovernanceManage");
        ai.MapPost("/thresholds/{thresholdPolicyId:long}/activate", async (
            HttpContext http,long thresholdPolicyId,long tenantId,AiGovernanceService service,StepUpAuthorizationService stepUp,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"ai.threshold.activate:{thresholdPolicyId}");
            if (!stepUp.HasRecentStepUp(http.User)) return AuraApiResults.Forbidden("Threshold activation requires recent MFA or step-up", 40395);
            var result = await service.ActivateThresholdAsync(thresholdPolicyId, tenantId, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "AI threshold activated", $"thresholdPolicyId={thresholdPolicyId}, tenantId={tenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "AI threshold activated");
        }).RequireAuthorization("AiReleaseApprove");
        ai.MapPost("/drift/calculate", async (
            HttpContext http,AiDriftCalculateRequest request,AiGovernanceService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "ai.drift.calculate");
            return ToResult(await service.CalculateDriftAsync(request, ct), "AI drift calculated");
        }).RequireAuthorization("AiGovernanceManage");
        ai.MapGet("/dashboard", async (
            HttpContext http,long tenantId,DateTimeOffset? from,DateTimeOffset? to,AiGovernanceService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "ai.dashboard");
            var end = to ?? DateTimeOffset.UtcNow;
            var data = await service.GetDashboardAsync(tenantId, from ?? end.AddDays(-30), end, ct);
            return Results.Ok(new { code = 0, msg = "Query succeeded", data });
        }).RequireAuthorization("AiGovernanceView");
    }

    private static void MapBreakGlass(RouteGroupBuilder api)
    {
        var emergency = api.MapGroup("/identity/break-glass");
        emergency.MapGet("", async (BreakGlassService service,CancellationToken ct) =>
            Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListAsync(ct) }))
            .RequireAuthorization("OpsView");
        emergency.MapPost("", async (
            HttpContext http,BreakGlassRegisterRequest request,BreakGlassService service,StepUpAuthorizationService stepUp,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!stepUp.HasRecentStepUp(http.User)) return AuraApiResults.Forbidden("Break-glass registration requires recent MFA or step-up", 40396);
            var result = await service.RegisterAsync(request, Actor(http), http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Break-glass account registered", $"userId={request.UserId}, custodian={request.CredentialCustodian}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Break-glass account registered");
        }).RequireAuthorization("OpsHighImpact");
        emergency.MapPost("/{accountId:long}/state", async (
            HttpContext http,long accountId,BreakGlassStateRequest request,BreakGlassService service,StepUpAuthorizationService stepUp,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!stepUp.HasRecentStepUp(http.User)) return AuraApiResults.Forbidden("Break-glass state changes require recent MFA or step-up", 40396);
            var result = await service.SetStateAsync(accountId, request, Actor(http), http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Break-glass state changed", $"accountId={accountId}, enabled={request.Enabled}, reason={request.Reason}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Break-glass state changed");
        }).RequireAuthorization("OpsHighImpact");
        emergency.MapPost("/{accountId:long}/exercise", async (
            HttpContext http,long accountId,BreakGlassExerciseRequest request,BreakGlassService service,
            AuditRepository audit,CancellationToken ct) =>
        {
            var result = await service.ExerciseAsync(accountId, request, Actor(http), http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Break-glass exercise recorded", $"accountId={accountId}, successful={request.Successful}, reason={request.Reason}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Break-glass exercise recorded");
        }).RequireAuthorization("OpsHighImpact");
        emergency.MapPost("/{accountId:long}/rotated", async (
            HttpContext http,long accountId,string reason,BreakGlassService service,StepUpAuthorizationService stepUp,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!stepUp.HasRecentStepUp(http.User)) return AuraApiResults.Forbidden("Credential rotation confirmation requires recent MFA or step-up", 40396);
            var result = await service.MarkRotatedAsync(accountId, reason, Actor(http), http.Connection.RemoteIpAddress?.ToString(), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Break-glass credential rotation recorded", $"accountId={accountId}, reason={reason}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Credential rotation recorded");
        }).RequireAuthorization("OpsHighImpact");
    }

    private static void MapProductInsights(RouteGroupBuilder api)
    {
        var adapters = api.MapGroup("/integrations/adapters");
        adapters.MapPost("/{adapterId:long}/contract-runs", async (
            HttpContext http,long adapterId,AdapterContractRunRequest request,ProductInsightsService service,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (adapterId != request.AdapterId) return AuraApiResults.BadRequest("Adapter ID mismatch", 40097);
            var result = await service.RunAdapterContractAsync(request, Actor(http), ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Adapter contract run recorded", $"adapterId={adapterId}, model={request.DeviceModel}, firmware={request.FirmwareVersion}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Adapter contract run recorded");
        }).RequireAuthorization("IntegrationTest");

        var analytics = api.MapGroup("/analytics");
        analytics.MapGet("/dashboard", async (
            HttpContext http,long tenantId,DateTimeOffset? from,DateTimeOffset? to,ProductInsightsService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "analytics.dashboard");
            var end = to ?? DateTimeOffset.UtcNow;
            try
            {
                var data = await service.GetBusinessDashboardAsync(tenantId, from ?? end.AddDays(-30), end, ct);
                return Results.Ok(new { code = 0, msg = "Query succeeded", data });
            }
            catch (ArgumentException ex) { return AuraApiResults.BadRequest(ex.Message, 40098); }
        }).RequireAuthorization("UsageView");
        analytics.MapPost("/events", async (
            HttpContext http,AnalyticsEventRequest request,ProductInsightsService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "analytics.event.create");
            return ToResult(await service.RecordAnalyticsAsync(request, Actor(http), ct), "Analytics event recorded");
        }).RequireAuthorization();

        var mobile = api.MapGroup("/mobile");
        var drafts = mobile.MapGroup("/drafts");
        drafts.MapGet("", async (
            HttpContext http,long tenantId,ProductInsightsService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "mobile.draft.list");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListDraftsAsync(tenantId, Actor(http), ct) });
        }).RequireAuthorization("CaseView");
        drafts.MapPost("", async (
            HttpContext http,MobileDraftWriteRequest request,ProductInsightsService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "mobile.draft.save");
            return ToResult(await service.SaveDraftAsync(request, Actor(http), ct), "Mobile draft saved");
        }).RequireAuthorization("CaseManage");
        drafts.MapPost("/{draftId:long}/sync", async (
            HttpContext http,long draftId,MobileDraftSyncRequest request,ProductInsightsService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, $"mobile.draft.sync:{draftId}");
            var result = await service.SyncDraftAsync(draftId, request, Actor(http), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Mobile draft synced", $"draftId={draftId}, tenantId={request.TenantId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Mobile draft synced");
        }).RequireAuthorization("CaseManage");

        mobile.MapGet("/tasks", async (
            HttpContext http,long tenantId,ProductInsightsService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "mobile.tasks.list");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.GetMobileTasksAsync(tenantId, Actor(http), ct) });
        }).RequireAuthorization("CaseView");

        mobile.MapGet("/push-config", (IConfiguration configuration) =>
        {
            var publicKey = configuration["CommercialProduct:Mobile:WebPushPublicKey"]?.Trim() ?? "";
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = new { enabled = publicKey.Length > 0, publicKey } });
        }).RequireAuthorization("CaseView");

        mobile.MapGet("/push-subscriptions", async (
            HttpContext http,long tenantId,ProductInsightsService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, "mobile.push.list");
            return Results.Ok(new { code = 0, msg = "Query succeeded", data = await service.ListPushSubscriptionsAsync(tenantId, Actor(http), ct) });
        }).RequireAuthorization("CaseView");

        mobile.MapPost("/push-subscriptions", async (
            HttpContext http,MobilePushSubscriptionRequest request,ProductInsightsService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "mobile.push.save");
            return ToResult(await service.SavePushSubscriptionAsync(request, Actor(http), ct), "Push subscription saved");
        }).RequireAuthorization("CaseManage");

        mobile.MapDelete("/push-subscriptions/{subscriptionId:long}", async (
            HttpContext http,long subscriptionId,long tenantId,ProductInsightsService service,
            TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"mobile.push.revoke:{subscriptionId}");
            return ToResult(await service.RevokePushSubscriptionAsync(tenantId, subscriptionId, Actor(http), ct), "Push subscription revoked");
        }).RequireAuthorization("CaseManage");

        mobile.MapPost("/deep-links", async (
            HttpContext http,MobileDeepLinkRequest request,ProductInsightsService service,TenantScopeAccessService access,
            AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct))
                return await TenantForbiddenAsync(http, audit, request.TenantId, "mobile.deep-link.create");
            return ToResult(await service.CreateDeepLinkAsync(request, ct), "Deep link created");
        }).RequireAuthorization("CaseView");

        mobile.MapPost("/cases/{caseId:long}/photos", async (
            HttpContext http,long caseId,long tenantId,IFormFile file,double? latitude,double? longitude,string? purpose,
            ProductInsightsService service,TenantScopeAccessService access,AuditRepository audit,CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, tenantId, ct))
                return await TenantForbiddenAsync(http, audit, tenantId, $"mobile.photo.upload:{caseId}");
            var result = await service.UploadCasePhotoAsync(
                tenantId, caseId, file, latitude, longitude, purpose, Actor(http), http.TraceIdentifier, ct);
            if (result.Status == ProductCommandStatus.Success)
                await audit.InsertOperationAsync(Actor(http), "Mobile case photo added", $"tenantId={tenantId}, caseId={caseId}, traceId={http.TraceIdentifier}");
            return ToResult(result, "Case photo added");
        }).DisableAntiforgery().RequireAuthorization("CaseManage");
    }

    private static async Task<IResult> TransitionEventAsync(
        HttpContext http,
        long tenantId,
        long eventId,
        string action,
        EventActionRequest request,
        EventCaseRepository repository,
        TenantScopeAccessService access,
        AuditRepository audit,
        CancellationToken cancellationToken)
    {
        if (!await access.CanAccessAsync(http.User, tenantId, cancellationToken))
            return await TenantForbiddenAsync(http, audit, tenantId, $"event.{action}:{eventId}");
        var command = new EventTransitionRequest(action, request.ExpectedVersion, request.ReasonCode, request.Detail);
        return ToResult(await repository.TransitionEventAsync(
            tenantId, eventId, command, Actor(http), http.TraceIdentifier, IdempotencyKey(http), cancellationToken), "事件状态已更新");
    }

    private static IResult ToResult(ProductCommandResult result, string successMessage) => result.Status switch
    {
        ProductCommandStatus.Success => Results.Ok(new { code = 0, msg = result.Message ?? successMessage, data = result.Data }),
        ProductCommandStatus.Duplicate => Results.Ok(new { code = 0, msg = result.Message ?? "请求已处理", data = result.Data, replayed = true }),
        ProductCommandStatus.NotFound => AuraApiResults.NotFound(result.Message ?? "资源不存在", 40401),
        ProductCommandStatus.Conflict => AuraApiResults.Conflict(result.Message ?? "资源版本冲突", 40901, new { currentVersion = result.CurrentVersion }),
        ProductCommandStatus.Forbidden => AuraApiResults.Forbidden(result.Message ?? "无权访问资源", 40301),
        _ => AuraApiResults.BadRequest(result.Message ?? "请求无效", 40001)
    };

    private static async Task<IResult> TenantForbiddenAsync(HttpContext http, AuditRepository audit, long tenantId, string action)
    {
        await audit.InsertOperationAsync(Actor(http), "租户越权拒绝", $"tenantId={tenantId}, action={action}, traceId={http.TraceIdentifier}");
        return AuraApiResults.Forbidden("无权访问该租户资源", 40301);
    }

    private static string Actor(HttpContext http) => http.User.Identity?.Name?.Trim() ?? "unknown";

    private static string? IdempotencyKey(HttpContext http)
    {
        var value = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value.Length <= 128 ? value : value[..128];
    }

    private static void SetEtag(HttpContext http, int version) => http.Response.Headers.ETag = $"\"{version}\"";
}
