using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Graph;

internal sealed class GraphRelationshipProjectionService(
    PgSqlConnectionFactory connectionFactory,
    IGraphRepository graph,
    IConfiguration configuration)
{
    public async Task ProjectAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var tenantId = Long(payload, "tenant_id");
        var personId = Text(payload, "entity_id");
        var eventTime = Date(payload, "event_time") ?? DateTimeOffset.UtcNow;
        if (!tenantId.HasValue || string.IsNullOrWhiteSpace(personId)) return;

        var lookbackHours = Math.Clamp(configuration.GetValue("Graph:Projection:RelationshipLookbackHours", 24), 1, 24 * 365);
        var from = eventTime.UtcDateTime.AddHours(-lookbackHours);
        var to = eventTime.UtcDateTime.AddHours(lookbackHours);
        await ProjectVisitsAsync(tenantId.Value, personId, from, to, cancellationToken);
        await ProjectCoOccurrencesAsync(tenantId.Value, personId, from, to, cancellationToken);
        await ProjectTransitionsAsync(tenantId.Value, personId, from, to, cancellationToken);
    }

    private async Task ProjectVisitsAsync(
        long tenantId,
        string personId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<VisitAggregate>(new CommandDefinition(
            """
            SELECT event.camera_id AS CameraId,roi.room_node_id AS RoomId,
              date_trunc('hour',event.event_time) AS BucketStart,COUNT(*) AS Count,
              MIN(event.event_time) AS FirstSeen,MAX(event.event_time) AS LastSeen
            FROM track_event event LEFT JOIN map_roi roi ON roi.roi_id=event.roi_id
            WHERE event.vid=@PersonId AND event.event_time BETWEEN @From AND @To
              AND EXISTS(SELECT 1 FROM media_source source
                WHERE source.camera_id=event.camera_id AND source.tenant_id=@TenantId)
            GROUP BY event.camera_id,roi.room_node_id,date_trunc('hour',event.event_time)
            """,
            new { TenantId = tenantId, PersonId = personId, From = from, To = to },
            cancellationToken: cancellationToken))).AsList();
        var personRef = GraphKeys.PersonRef(tenantId, personId);
        foreach (var row in rows)
        {
            await graph.UpsertVertexAsync("cameras", GraphKeys.Camera(tenantId, row.CameraId),
                new { tenant_id = tenantId, camera_id = row.CameraId, source_version = "relationship-1" }, cancellationToken);
            var cameraRef = GraphKeys.CameraRef(tenantId, row.CameraId);
            await graph.UpsertEdgeAsync("visited",
                ArangoGraphRepository.StableEdgeKey(tenantId, "visited", personRef, cameraRef, row.BucketStart.ToUniversalTime().ToString("O")),
                personRef, cameraRef, EdgeDocument(tenantId, row), cancellationToken);
            if (row.RoomId.HasValue)
            {
                await graph.UpsertVertexAsync("rooms", GraphKeys.Node(tenantId, row.RoomId.Value),
                    new { tenant_id = tenantId, node_id = row.RoomId.Value, source_version = "relationship-1" }, cancellationToken);
                var roomRef = GraphKeys.NodeRef("rooms", tenantId, row.RoomId.Value);
                await graph.UpsertEdgeAsync("visited",
                    ArangoGraphRepository.StableEdgeKey(tenantId, "visited", personRef, roomRef, row.BucketStart.ToUniversalTime().ToString("O")),
                    personRef, roomRef, EdgeDocument(tenantId, row), cancellationToken);
            }
        }
    }

    private async Task ProjectCoOccurrencesAsync(
        long tenantId,
        string personId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var windowSeconds = Math.Clamp(configuration.GetValue("Graph:Projection:CoOccurrenceWindowSeconds", 300), 1, 3600);
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<CoOccurrenceAggregate>(new CommandDefinition(
            """
            SELECT other.vid AS OtherPersonId,date_trunc('hour',target.event_time) AS BucketStart,
              COUNT(*) AS Count,MIN(LEAST(target.event_time,other.event_time)) AS FirstSeen,
              MAX(GREATEST(target.event_time,other.event_time)) AS LastSeen
            FROM track_event target
            JOIN track_event other ON other.vid<>target.vid
              AND other.camera_id=target.camera_id
              AND other.roi_id IS NOT DISTINCT FROM target.roi_id
              AND ABS(EXTRACT(EPOCH FROM (other.event_time-target.event_time)))<=@WindowSeconds
            WHERE target.vid=@PersonId AND target.event_time BETWEEN @From AND @To
              AND LEFT(target.vid,2)<>'T_' AND LEFT(other.vid,2)<>'T_'
              AND EXISTS(SELECT 1 FROM media_source source
                WHERE source.camera_id=target.camera_id AND source.tenant_id=@TenantId)
            GROUP BY other.vid,date_trunc('hour',target.event_time)
            """,
            new { TenantId = tenantId, PersonId = personId, From = from, To = to, WindowSeconds = windowSeconds },
            cancellationToken: cancellationToken))).AsList();
        foreach (var row in rows)
        {
            await graph.UpsertVertexAsync("persons", GraphKeys.Person(tenantId, row.OtherPersonId),
                new { tenant_id = tenantId, person_id = row.OtherPersonId, source_version = "relationship-1" }, cancellationToken);
            var left = string.CompareOrdinal(personId, row.OtherPersonId) <= 0 ? personId : row.OtherPersonId;
            var right = left == personId ? row.OtherPersonId : personId;
            var fromRef = GraphKeys.PersonRef(tenantId, left);
            var toRef = GraphKeys.PersonRef(tenantId, right);
            await graph.UpsertEdgeAsync("co_occurs",
                ArangoGraphRepository.StableEdgeKey(tenantId, "co_occurs", fromRef, toRef, row.BucketStart.ToUniversalTime().ToString("O")),
                fromRef, toRef, new
                {
                    tenant_id = tenantId,
                    bucket_start = row.BucketStart,
                    row.Count,
                    first_seen = row.FirstSeen,
                    last_seen = row.LastSeen,
                    window_seconds = windowSeconds,
                    source_version = "relationship-1"
                }, cancellationToken);
        }
    }

    private async Task ProjectTransitionsAsync(
        long tenantId,
        string personId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var maxGapSeconds = Math.Clamp(configuration.GetValue("Graph:Projection:TransitionMaxGapSeconds", 1800), 1, 86400);
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<TransitionAggregate>(new CommandDefinition(
            """
            WITH ordered AS (
              SELECT event.camera_id,event.event_time,
                LAG(event.camera_id) OVER(ORDER BY event.event_time,event.event_id) AS previous_camera_id,
                LAG(event.event_time) OVER(ORDER BY event.event_time,event.event_id) AS previous_time
              FROM track_event event
              WHERE event.vid=@PersonId AND event.event_time BETWEEN @From AND @To
                AND EXISTS(SELECT 1 FROM media_source source
                  WHERE source.camera_id=event.camera_id AND source.tenant_id=@TenantId))
            SELECT previous_camera_id AS FromCameraId,camera_id AS ToCameraId,
              date_trunc('hour',event_time) AS BucketStart,COUNT(*) AS Count,
              MIN(previous_time) AS FirstSeen,MAX(event_time) AS LastSeen
            FROM ordered WHERE previous_camera_id IS NOT NULL AND previous_camera_id<>camera_id
              AND EXTRACT(EPOCH FROM (event_time-previous_time)) BETWEEN 0 AND @MaxGapSeconds
            GROUP BY previous_camera_id,camera_id,date_trunc('hour',event_time)
            """,
            new { TenantId = tenantId, PersonId = personId, From = from, To = to, MaxGapSeconds = maxGapSeconds },
            cancellationToken: cancellationToken))).AsList();
        foreach (var row in rows)
        {
            var fromRef = GraphKeys.CameraRef(tenantId, row.FromCameraId);
            var toRef = GraphKeys.CameraRef(tenantId, row.ToCameraId);
            await graph.UpsertEdgeAsync("transition",
                ArangoGraphRepository.StableEdgeKey(tenantId, "transition", fromRef, toRef, row.BucketStart.ToUniversalTime().ToString("O")),
                fromRef, toRef, new
                {
                    tenant_id = tenantId,
                    person_id = personId,
                    bucket_start = row.BucketStart,
                    row.Count,
                    first_seen = row.FirstSeen,
                    last_seen = row.LastSeen,
                    max_gap_seconds = maxGapSeconds,
                    source_version = "relationship-1"
                }, cancellationToken);
        }
    }

    private static object EdgeDocument(long tenantId, VisitAggregate row) => new
    {
        tenant_id = tenantId,
        bucket_start = row.BucketStart,
        row.Count,
        first_seen = row.FirstSeen,
        last_seen = row.LastSeen,
        source_version = "relationship-1"
    };

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static long? Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) ? result : null;
    private static DateTimeOffset? Date(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), out var result) ? result : null;

    private sealed record VisitAggregate(long CameraId, long? RoomId, DateTime BucketStart, long Count, DateTime FirstSeen, DateTime LastSeen);
    private sealed record CoOccurrenceAggregate(string OtherPersonId, DateTime BucketStart, long Count, DateTime FirstSeen, DateTime LastSeen);
    private sealed record TransitionAggregate(long FromCameraId, long ToCameraId, DateTime BucketStart, long Count, DateTime FirstSeen, DateTime LastSeen);
}
