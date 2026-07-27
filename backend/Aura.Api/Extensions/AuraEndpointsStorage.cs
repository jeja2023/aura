using Aura.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsStorage
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/storage/{**relativePath}", async (
            HttpContext http,
            string? relativePath,
            ProtectedStorageService storage,
            CancellationToken cancellationToken) =>
                await storage.DownloadAsync(http, relativePath, cancellationToken))
            .RequireAuthorization();
    }
}
