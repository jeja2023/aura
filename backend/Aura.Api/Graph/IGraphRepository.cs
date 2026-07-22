using System.Text.Json;

namespace Aura.Api.Graph;

internal interface IGraphRepository
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken);
    Task ResetAsync(CancellationToken cancellationToken);
    Task ProjectEventAsync(JsonElement payload, CancellationToken cancellationToken);
    Task UpsertVertexAsync(string collection, string key, object document, CancellationToken cancellationToken);
    Task UpsertEdgeAsync(string collection, string key, string from, string to, object document, CancellationToken cancellationToken);
    Task<JsonElement> QueryAsync(string operation, string aql, object bindVars, CancellationToken cancellationToken);
    Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken);
}

internal sealed record GraphHealth(bool Available, string Database, string Graph, string Version, string? Error);
internal sealed record GraphPathRequest(long TenantId, long FromCameraId, long ToCameraId, int MaxDepth = 8, int Limit = 10);
internal sealed record GraphReachabilityRequest(long TenantId, long CameraId, int Depth = 2, int Limit = 100);
internal sealed record PersonGraphQuery(long TenantId, string PersonId, DateTimeOffset? From = null, DateTimeOffset? To = null, int Depth = 1, int Limit = 100);
internal sealed record RoomGraphQuery(long TenantId, long RoomId, DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 100);
