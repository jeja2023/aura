using System.Text.Json;
using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Aura.Api.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsVectorIndex
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vector-index").WithTags("Vector index");
        group.MapPost("/embeddings", async (HttpContext http, VectorUpsertRequest request, VectorIndexRouter index, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (request.TenantId <= 0 || request.ModelId <= 0 || string.IsNullOrWhiteSpace(request.Vid))
                return AuraApiResults.BadRequest("tenantId, modelId and vid are required.");
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct)) return Forbidden();
            try
            {
                var id = await index.UpsertAsync(new VectorIndexDocument(
                    request.TenantId,
                    request.ModelId,
                    request.Vid,
                    request.CaptureId,
                    request.ExternalEmbeddingId,
                    request.Feature,
                    request.Metadata?.GetRawText() ?? "{}"), ct);
                return Results.Ok(new { code = 0, msg = "ok", data = new { embeddingId = id, engine = index.Engine } });
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40072);
            }
        }).RequireAuthorization("VectorIndexManage");

        group.MapPost("/search", async (HttpContext http, VectorIndexSearchRequest request, VectorIndexRouter index, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (request.TenantId <= 0 || request.ModelId <= 0)
                return AuraApiResults.BadRequest("tenantId and modelId are required.");
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct)) return Forbidden();
            try
            {
                var rows = await index.SearchAsync(new VectorIndexQuery(
                    request.TenantId,
                    request.ModelId,
                    request.Feature,
                    request.TopK,
                    request.MinScore,
                    request.Vid), ct);
                return Results.Ok(new { code = 0, msg = "ok", data = rows });
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40072);
            }
        }).RequireAuthorization("MediaAnalysisView");

        group.MapGet("/status", async (HttpContext http, long? tenantId, long? modelId, VectorIndexRouter index, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessOptionalAsync(http, tenantId, access, ct)
                ? Results.Ok(new { code = 0, msg = "ok", data = await index.GetStatusAsync(tenantId, modelId, ct) })
                : Forbidden())
            .RequireAuthorization("MediaAnalysisView");

        group.MapPost("/migrations/backfill", async (HttpContext http, VectorBackfillRequest request, VectorMigrationService service, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessAsync(http.User, request.TenantId, ct)) return Forbidden();
            try
            {
                return Results.Ok(new { code = 0, msg = "ok", data = await service.BackfillAsync(request, ct) });
            }
            catch (ArgumentException ex)
            {
                return AuraApiResults.BadRequest(ex.Message, 40073);
            }
        }).RequireAuthorization("VectorIndexManage");

        group.MapPost("/migrations/shadow-evaluate", async (HttpContext http, VectorShadowEvaluationRequest request, VectorMigrationService service, TenantScopeAccessService access, CancellationToken ct) =>
            await access.CanAccessAsync(http.User, request.TenantId, ct)
                ? Results.Ok(new { code = 0, msg = "ok", data = await service.EvaluateAsync(request, ct) })
                : Forbidden())
            .RequireAuthorization("VectorIndexManage");

        group.MapGet("/migrations", async (HttpContext http, string? migrationName, VectorMigrationService service, CancellationToken ct) =>
            TenantScopeAccessService.IsSuperAdmin(http.User)
                ? Results.Ok(new { code = 0, msg = "ok", data = await service.GetMigrationStatusAsync(migrationName, ct) })
                : Forbidden())
            .RequireAuthorization("VectorIndexManage");
    }

    private static IResult Forbidden() => AuraApiResults.Forbidden("The requested vector operation is outside the current tenant scope.");
    private static async Task<bool> CanAccessOptionalAsync(HttpContext http, long? tenantId, TenantScopeAccessService access, CancellationToken ct) =>
        tenantId.HasValue
            ? await access.CanAccessAsync(http.User, tenantId.Value, ct)
            : TenantScopeAccessService.IsSuperAdmin(http.User);
}
