using System.Security.Claims;
using Aura.Api.Graph;
using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsGraph
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/graph").WithTags("Graph");
        group.MapPost("/cameras/reachable", async (HttpContext http, GraphReachabilityRequest request, GraphQueryService service, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessAsync(http, request.TenantId, access, ct)
                ? Ok(await service.ReachableCamerasAsync(request, ct))
                : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapPost("/cameras/paths", async (HttpContext http, GraphPathRequest request, GraphQueryService service, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessAsync(http, request.TenantId, access, ct)
                ? Ok(await service.CameraPathsAsync(request, ct))
                : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapPost("/persons/visits", async (HttpContext http, PersonGraphQuery request, GraphQueryService service, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessAsync(http, request.TenantId, access, ct)
                ? Ok(await service.PersonVisitsAsync(request, ct))
                : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapPost("/persons/co-occurrences", async (HttpContext http, PersonGraphQuery request, GraphQueryService service, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessAsync(http, request.TenantId, access, ct)
                ? Ok(await service.PersonCoOccurrencesAsync(request, ct))
                : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapPost("/rooms/people", async (HttpContext http, RoomGraphQuery request, GraphQueryService service, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessAsync(http, request.TenantId, access, ct)
                ? Ok(await service.RoomPeopleAsync(request, ct))
                : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapGet("/health", async (HttpContext http, IGraphRepository graph, CancellationToken ct) =>
            TenantScopeAccessService.IsSuperAdmin(http.User) ? Ok(await graph.GetHealthAsync(ct)) : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapGet("/projection", async (HttpContext http, GraphProjectionRepository repository, CancellationToken ct) =>
            TenantScopeAccessService.IsSuperAdmin(http.User) ? Ok(await repository.GetStatusAsync(ct)) : Forbidden())
            .RequireAuthorization("GraphView");
        group.MapGet("/projection/outbox", async (HttpContext http, long? tenantId, string? status, int? limit, GraphProjectionRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessOptionalAsync(http, tenantId, access, ct)
                ? Ok(await repository.QueryAsync(tenantId, status, limit ?? 100, ct))
                : Forbidden())
            .RequireAuthorization("GraphAdmin");
        group.MapPost("/projection/replay", async (HttpContext http, GraphReplayRequest request, GraphProjectionRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
            await CanAccessOptionalAsync(http, request.TenantId, access, ct)
                ? Ok(new { replayed = await repository.ReplayAsync(request.TenantId, request.OutboxIds, request.Status, request.Limit, ct) })
                : Forbidden())
            .RequireAuthorization("GraphAdmin");
        group.MapPost("/rebuild", async (ClaimsPrincipal user, GraphProjectionRepository repository, CancellationToken ct) =>
        {
            if (!TenantScopeAccessService.IsSuperAdmin(user)) return Forbidden();
            var id = await repository.CreateRebuildAsync(user.Identity?.Name, ct);
            return Results.Accepted($"/api/graph/rebuild/{id}", new { code = 0, msg = "accepted", data = new { rebuildId = id } });
        }).RequireAuthorization("GraphAdmin");
    }

    private static IResult Ok(object? data) => Results.Ok(new { code = 0, msg = "ok", data });
    private static IResult Forbidden() => AuraApiResults.Forbidden("The requested graph operation is outside the current tenant scope.");
    private static Task<bool> CanAccessAsync(HttpContext http, long tenantId, TenantScopeAccessService access, CancellationToken ct) =>
        access.CanAccessAsync(http.User, tenantId, ct);
    private static async Task<bool> CanAccessOptionalAsync(HttpContext http, long? tenantId, TenantScopeAccessService access, CancellationToken ct) =>
        tenantId.HasValue
            ? await access.CanAccessAsync(http.User, tenantId.Value, ct)
            : TenantScopeAccessService.IsSuperAdmin(http.User);
}
