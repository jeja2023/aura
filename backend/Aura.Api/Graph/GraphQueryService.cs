using System.Text.Json;

namespace Aura.Api.Graph;

internal sealed class GraphQueryService(IGraphRepository graph)
{
    public Task<JsonElement> ReachableCamerasAsync(GraphReachabilityRequest request, CancellationToken cancellationToken) =>
        graph.QueryAsync(
            "camera_reachable",
            """
            FOR vertex, edge, path IN 1..@depth OUTBOUND @start connects
              OPTIONS { bfs: true, uniqueVertices: 'global' }
              FILTER vertex.tenant_id == @tenantId
              LIMIT @limit
              RETURN { camera: vertex, depth: LENGTH(path.edges), path: path.edges[*].relation_type }
            """,
            new
            {
                depth = Math.Clamp(request.Depth, 1, 10),
                start = GraphKeys.CameraRef(request.TenantId, request.CameraId),
                tenantId = request.TenantId,
                limit = Math.Clamp(request.Limit, 1, 1000)
            }, cancellationToken);

    public Task<JsonElement> CameraPathsAsync(GraphPathRequest request, CancellationToken cancellationToken) =>
        graph.QueryAsync(
            "camera_paths",
            """
            FOR path IN OUTBOUND K_SHORTEST_PATHS @from TO @to connects
              OPTIONS { weightAttribute: 'weight', defaultWeight: 1 }
              FILTER path.vertices[*].tenant_id ALL == @tenantId
                AND LENGTH(path.edges) <= @maxDepth
              LIMIT @limit
              RETURN { distance: SUM(path.edges[*].weight), cameras: path.vertices[*].camera_id, edges: path.edges[*].relation_type }
            """,
            new
            {
                from = GraphKeys.CameraRef(request.TenantId, request.FromCameraId),
                to = GraphKeys.CameraRef(request.TenantId, request.ToCameraId),
                tenantId = request.TenantId,
                maxDepth = Math.Clamp(request.MaxDepth, 1, 20),
                limit = Math.Clamp(request.Limit, 1, 100)
            }, cancellationToken);

    public Task<JsonElement> PersonVisitsAsync(PersonGraphQuery request, CancellationToken cancellationToken) =>
        graph.QueryAsync(
            "person_visits",
            """
            FOR vertex, edge IN 1..1 OUTBOUND @person visited
              FILTER edge.tenant_id == @tenantId
                AND (@from == null OR edge.last_seen >= @from)
                AND (@to == null OR edge.first_seen <= @to)
              SORT edge.last_seen DESC
              LIMIT @limit
              RETURN { place: vertex, visit: edge }
            """,
            new
            {
                person = GraphKeys.PersonRef(request.TenantId, request.PersonId),
                tenantId = request.TenantId,
                from = request.From,
                to = request.To,
                limit = Math.Clamp(request.Limit, 1, 1000)
            }, cancellationToken);

    public Task<JsonElement> PersonCoOccurrencesAsync(PersonGraphQuery request, CancellationToken cancellationToken) =>
        graph.QueryAsync(
            "person_co_occurrences",
            """
            FOR vertex, edge, path IN 1..@depth ANY @person co_occurs
              OPTIONS { bfs: true, uniqueVertices: 'global' }
              FILTER vertex.tenant_id == @tenantId
                AND (@from == null OR edge.last_seen >= @from)
                AND (@to == null OR edge.first_seen <= @to)
              SORT edge.count DESC
              LIMIT @limit
              RETURN { person: vertex, relation: edge, depth: LENGTH(path.edges) }
            """,
            new
            {
                person = GraphKeys.PersonRef(request.TenantId, request.PersonId),
                tenantId = request.TenantId,
                depth = Math.Clamp(request.Depth, 1, 2),
                from = request.From,
                to = request.To,
                limit = Math.Clamp(request.Limit, 1, 1000)
            }, cancellationToken);

    public Task<JsonElement> RoomPeopleAsync(RoomGraphQuery request, CancellationToken cancellationToken) =>
        graph.QueryAsync(
            "room_people",
            """
            FOR person, edge IN 1..1 INBOUND @room visited
              FILTER edge.tenant_id == @tenantId
                AND (@from == null OR edge.last_seen >= @from)
                AND (@to == null OR edge.first_seen <= @to)
              SORT edge.last_seen DESC
              LIMIT @limit
              RETURN { person, visit: edge }
            """,
            new
            {
                room = GraphKeys.NodeRef("rooms", request.TenantId, request.RoomId),
                tenantId = request.TenantId,
                from = request.From,
                to = request.To,
                limit = Math.Clamp(request.Limit, 1, 1000)
            }, cancellationToken);
}
