using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Graph;
using Aura.Api.Vector;
using Dapper;

namespace Aura.Api.Product;

internal sealed class InvestigationService(
    InvestigationRepository repository,
    VectorIndexRouter vectorIndex,
    GraphQueryService graphQuery,
    PgSqlConnectionFactory connectionFactory,
    ILogger<InvestigationService> logger)
{
    public async Task<ProductCommandResult> RunQueryAsync(
        long tenantId,
        long investigationId,
        InvestigationQueryRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var queryId = await repository.StartQueryAsync(tenantId, investigationId, request, actor, cancellationToken);
        if (!queryId.HasValue) return new(ProductCommandStatus.NotFound, Message: "调查不存在或已结束");
        try
        {
            var result = await ExecuteAsync(tenantId, investigationId, request, cancellationToken);
            var json = JsonSerializer.Serialize(result);
            await repository.CompleteQueryAsync(tenantId, queryId.Value, "completed", json, cancellationToken);
            return ProductCommandResult.Ok(new { queryId, status = "completed", result });
        }
        catch (OperationCanceledException)
        {
            await repository.CompleteQueryAsync(tenantId, queryId.Value, "cancelled", "{}", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "调查查询失败。tenantId={TenantId}, investigationId={InvestigationId}, queryType={QueryType}", tenantId, investigationId, request.QueryType);
            var failure = JsonSerializer.Serialize(new { error = ex.Message, partial = false });
            await repository.CompleteQueryAsync(tenantId, queryId.Value, "failed", failure, CancellationToken.None);
            return new(ProductCommandStatus.Invalid, new { queryId }, "查询执行失败，请检查条件或依赖状态");
        }
    }

    private async Task<object> ExecuteAsync(long tenantId, long investigationId, InvestigationQueryRequest request, CancellationToken cancellationToken)
    {
        var queryType = request.QueryType.Trim().ToLowerInvariant();
        var query = request.Query;
        return queryType switch
        {
            "similarity" => await SimilarityAsync(tenantId, query, cancellationToken),
            "person_visits" => await graphQuery.PersonVisitsAsync(new PersonGraphQuery(
                tenantId, RequiredString(query, "personId"), OptionalDate(query, "from"), OptionalDate(query, "to"), 1, OptionalInt(query, "limit", 100)), cancellationToken),
            "co_occurrence" => await graphQuery.PersonCoOccurrencesAsync(new PersonGraphQuery(
                tenantId, RequiredString(query, "personId"), OptionalDate(query, "from"), OptionalDate(query, "to"), OptionalInt(query, "depth", 1), OptionalInt(query, "limit", 100)), cancellationToken),
            "room_people" => await graphQuery.RoomPeopleAsync(new RoomGraphQuery(
                tenantId, RequiredLong(query, "roomId"), OptionalDate(query, "from"), OptionalDate(query, "to"), OptionalInt(query, "limit", 100)), cancellationToken),
            "camera_reachable" => await graphQuery.ReachableCamerasAsync(new GraphReachabilityRequest(
                tenantId, RequiredLong(query, "cameraId"), OptionalInt(query, "depth", 2), OptionalInt(query, "limit", 100)), cancellationToken),
            "camera_paths" => await graphQuery.CameraPathsAsync(new GraphPathRequest(
                tenantId, RequiredLong(query, "fromCameraId"), RequiredLong(query, "toCameraId"), OptionalInt(query, "maxDepth", 8), OptionalInt(query, "limit", 10)), cancellationToken),
            "timeline" => new
            {
                progressiveEndpoint = $"/api/v1/investigations/{investigationId}/timeline?tenantId={tenantId}",
                filters = query
            },
            "candidate_people" => await CandidatePeopleAsync(tenantId, query, cancellationToken),
            _ => throw new ArgumentException("不支持的调查查询类型")
        };
    }

    private async Task<object> CandidatePeopleAsync(long tenantId, JsonElement query, CancellationToken cancellationToken)
    {
        var from = OptionalDate(query, "from");
        var to = OptionalDate(query, "to");
        var occurrenceMin = Math.Clamp(OptionalInt(query, "occurrenceMin", 1), 1, 100_000);
        var building = OptionalString(query, "building");
        var floor = OptionalString(query, "floor");
        var caseReference = RequiredString(query, "caseReference");
        var strangerOnly = query.TryGetProperty("strangerOnly", out var strangerNode) && strangerNode.ValueKind == JsonValueKind.True;
        var limit = Math.Clamp(OptionalInt(query, "limit", 100), 1, 500);
        await using var connection = connectionFactory.CreateConnection();
        var matchingCases = (await connection.QueryAsync<long>(new CommandDefinition(
            """
            SELECT case_id FROM incident_case
            WHERE tenant_id=@TenantId AND (case_no ILIKE @Exact OR title ILIKE @Exact)
            ORDER BY updated_at DESC LIMIT 2
            """, new { TenantId = tenantId, Exact = caseReference }, cancellationToken: cancellationToken))).AsList();
        if (matchingCases.Count == 0) throw new ArgumentException("Referenced case was not found in the current tenant");
        if (matchingCases.Count > 1) throw new ArgumentException("Referenced case is ambiguous; use an exact case number");
        var casePeople = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT e.entity_ref
            FROM incident_case_event ce JOIN business_event e ON e.event_id=ce.event_id AND e.tenant_id=ce.tenant_id
            WHERE ce.tenant_id=@TenantId AND ce.case_id=@CaseId AND ce.active AND e.entity_ref IS NOT NULL
            """, new { TenantId = tenantId, CaseId = matchingCases[0] }, cancellationToken: cancellationToken))).AsList();
        if (casePeople.Count == 0) throw new ArgumentException("Referenced case has no person entities available for co-occurrence filtering");
        var events = (await connection.QueryAsync<CandidateEventRow>(new CommandDefinition(
            """
            SELECT event_id AS EventId,event_no AS EventNo,entity_ref AS EntityRef,space_ref AS SpaceRef,
              occurrence_count AS OccurrenceCount,first_occurred_at AS FirstOccurredAt,last_occurred_at AS LastOccurredAt,
              representative_evidence_json::text AS EvidenceJson
            FROM business_event
            WHERE tenant_id=@TenantId AND entity_ref IS NOT NULL
              AND (@From IS NULL OR last_occurred_at>=@From) AND (@To IS NULL OR first_occurred_at<=@To)
              AND occurrence_count>=@OccurrenceMin
              AND (@Building IS NULL OR space_ref ILIKE '%'||@Building||'%')
              AND (@Floor IS NULL OR space_ref ILIKE '%'||@Floor||'%')
              AND (NOT @StrangerOnly OR event_type ILIKE '%stranger%' OR rule_code ILIKE '%stranger%'
                   OR title ILIKE '%陌生%' OR summary ILIKE '%陌生%')
            ORDER BY last_occurred_at DESC LIMIT 1000
            """, new
            {
                TenantId = tenantId, From = from, To = to, OccurrenceMin = occurrenceMin,
                Building = building, Floor = floor, StrangerOnly = strangerOnly
            }, cancellationToken: cancellationToken))).AsList();
        var candidates = events.Select(row => row.EntityRef).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            return new { classification = "candidate", confirmed = false, candidates = Array.Empty<object>(), evidence = Array.Empty<object>(), caseId = matchingCases[0] };
        var graphResult = await graphQuery.CandidatePeopleAsync(new CandidatePeopleGraphQuery(
            tenantId, candidates, casePeople, from, to, 1, limit), cancellationToken);
        return new
        {
            classification = "candidate",
            confirmed = false,
            caseId = matchingCases[0],
            casePersonCount = casePeople.Count,
            graphCandidates = graphResult,
            evidence = events.Select(row => new
            {
                row.EventId,row.EventNo,row.EntityRef,row.SpaceRef,row.OccurrenceCount,
                row.FirstOccurredAt,row.LastOccurredAt,evidence = JsonSerializer.Deserialize<JsonElement>(row.EvidenceJson)
            }),
            permissionTrimmed = true,
            statement = "Results are candidates supported by event and graph references; a user must confirm any conclusion."
        };
    }

    private async Task<object> SimilarityAsync(long tenantId, JsonElement query, CancellationToken cancellationToken)
    {
        var featureNode = query.TryGetProperty("feature", out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new ArgumentException("相似检索缺少 feature 数组");
        var feature = featureNode.EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (feature.Length is 0 or > 4096) throw new ArgumentException("feature 维度无效");
        var modelId = RequiredLong(query, "modelId");
        var hits = await vectorIndex.SearchAsync(new VectorIndexQuery(
            tenantId, modelId, feature, OptionalInt(query, "topK", 20), OptionalDouble(query, "minScore"), OptionalString(query, "vid")), cancellationToken);
        return new { engine = vectorIndex.Engine, modelId, hits };
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"缺少 {name}");
    private static long RequiredLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : throw new ArgumentException($"缺少 {name}");
    private static int OptionalInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static double? OptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? OptionalDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;
    private sealed record CandidateEventRow(
        long EventId,string EventNo,string EntityRef,string? SpaceRef,int OccurrenceCount,
        DateTimeOffset FirstOccurredAt,DateTimeOffset LastOccurredAt,string EvidenceJson);
}
