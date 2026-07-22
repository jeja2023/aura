using Aura.Api.Data;
using Dapper;
using Aura.Api.MediaAnalysis;

namespace Aura.Api.Graph;

internal sealed class GraphRebuildService(
    GraphProjectionRepository projectionRepository,
    IGraphRepository graph,
    IConfiguration configuration,
    ILogger<GraphRebuildService> logger)
{
    public async Task<(long Vertices, long Edges)> RebuildAsync(CancellationToken cancellationToken)
    {
        await graph.ResetAsync(cancellationToken);
        await using var connection = projectionRepository.ConnectionFactory.CreateConnection();
        var defaultTenantId = Math.Max(1, configuration.GetValue("Graph:DefaultTenantId", 1L));
        long vertices = 0;
        long edges = 0;
        var tenantIds = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT tenant_id FROM tenant_project WHERE enabled=TRUE ORDER BY tenant_id",
            cancellationToken: cancellationToken))).AsList();
        if (tenantIds.Count == 0) tenantIds.Add(defaultTenantId);

        var nodes = (await connection.QueryAsync<CampusNode>(new CommandDefinition(
            "SELECT node_id AS NodeId,parent_id AS ParentId,level_type AS LevelType,node_name AS NodeName FROM dict_campus ORDER BY node_id",
            cancellationToken: cancellationToken))).AsList();
        var nodeCollections = nodes.ToDictionary(item => item.NodeId, item => NodeCollection(item.LevelType));
        var floors = (await connection.QueryAsync<FloorRow>(new CommandDefinition(
            "SELECT floor_id AS FloorId,node_id AS NodeId,file_path AS FilePath,scale_ratio AS ScaleRatio FROM map_floor",
            cancellationToken: cancellationToken))).AsList();
        var cameras = (await connection.QueryAsync<CameraRow>(new CommandDefinition(
            """
            SELECT camera.camera_id AS CameraId,camera.floor_id AS FloorId,camera.device_id AS DeviceId,
              camera.channel_no AS ChannelNo,camera.pos_x AS PosX,camera.pos_y AS PosY,
              device.name AS DeviceName,device.status AS DeviceStatus
            FROM map_camera camera LEFT JOIN nvr_device device ON device.device_id=camera.device_id
            """, cancellationToken: cancellationToken))).AsList();
        var rois = (await connection.QueryAsync<RoiRow>(new CommandDefinition(
            "SELECT roi_id AS RoiId,camera_id AS CameraId,room_node_id AS RoomNodeId,vertices_json::text AS VerticesJson FROM map_roi",
            cancellationToken: cancellationToken))).AsList();
        var topology = (await connection.QueryAsync<TopologyRow>(new CommandDefinition(
            "SELECT edge_id AS EdgeId,from_camera_id AS FromCameraId,to_camera_id AS ToCameraId,relation_type AS RelationType,weight AS Weight FROM space_topology_edge WHERE enabled=TRUE AND (valid_from IS NULL OR valid_from<=NOW()) AND (valid_to IS NULL OR valid_to>NOW())",
            cancellationToken: cancellationToken))).AsList();

        foreach (var tenantId in tenantIds)
        {
            foreach (var node in nodes)
            {
                var collection = nodeCollections[node.NodeId];
                await graph.UpsertVertexAsync(collection, GraphKeys.Node(tenantId, node.NodeId), new
                {
                    tenant_id = tenantId,
                    node_id = node.NodeId,
                    level_type = node.LevelType,
                    name = node.NodeName,
                    source_version = "rebuild-2",
                    updated_at = DateTimeOffset.UtcNow
                }, cancellationToken);
                vertices++;
                if (node.ParentId.HasValue && nodeCollections.TryGetValue(node.ParentId.Value, out var parentCollection))
                {
                    var parent = GraphKeys.NodeRef(parentCollection, tenantId, node.ParentId.Value);
                    var child = GraphKeys.NodeRef(collection, tenantId, node.NodeId);
                    await graph.UpsertEdgeAsync("contains", ArangoGraphRepository.StableEdgeKey(tenantId, "contains", parent, child),
                        parent, child, Version(tenantId), cancellationToken);
                    edges++;
                }
            }

            foreach (var floor in floors)
            {
                var floorRef = GraphKeys.FloorRef(tenantId, floor.FloorId);
                await graph.UpsertVertexAsync("floors", GraphKeys.Floor(tenantId, floor.FloorId), new
                {
                    tenant_id = tenantId,
                    floor_id = floor.FloorId,
                    node_id = floor.NodeId,
                    floor_plan_ref = floor.FilePath,
                    scale_ratio = floor.ScaleRatio,
                    source_version = "rebuild-2",
                    updated_at = DateTimeOffset.UtcNow
                }, cancellationToken);
                vertices++;
                var nodeRef = GraphKeys.NodeRef("floors", tenantId, floor.NodeId);
                await graph.UpsertEdgeAsync("located_in", ArangoGraphRepository.StableEdgeKey(tenantId, "floor_node", floorRef, nodeRef),
                    floorRef, nodeRef, Version(tenantId), cancellationToken);
                edges++;
            }

            foreach (var camera in cameras)
            {
                var cameraRef = GraphKeys.CameraRef(tenantId, camera.CameraId);
                await graph.UpsertVertexAsync("cameras", GraphKeys.Camera(tenantId, camera.CameraId), new
                {
                    tenant_id = tenantId,
                    camera_id = camera.CameraId,
                    floor_id = camera.FloorId,
                    device_id = camera.DeviceId,
                    channel_no = camera.ChannelNo,
                    pos_x = camera.PosX,
                    pos_y = camera.PosY,
                    device_name = camera.DeviceName,
                    status = camera.DeviceStatus,
                    source_version = "rebuild-2",
                    updated_at = DateTimeOffset.UtcNow
                }, cancellationToken);
                vertices++;
                var floorRef = GraphKeys.FloorRef(tenantId, camera.FloorId);
                await graph.UpsertEdgeAsync("located_in", ArangoGraphRepository.StableEdgeKey(tenantId, "located_in", cameraRef, floorRef),
                    cameraRef, floorRef, Version(tenantId), cancellationToken);
                edges++;
            }

            foreach (var roi in rois)
            {
                var cameraRef = GraphKeys.CameraRef(tenantId, roi.CameraId);
                var roiRef = GraphKeys.RoiRef(tenantId, roi.RoiId);
                var roomRef = GraphKeys.NodeRef("rooms", tenantId, roi.RoomNodeId);
                await graph.UpsertVertexAsync("rois", GraphKeys.Roi(tenantId, roi.RoiId), new
                {
                    tenant_id = tenantId,
                    roi_id = roi.RoiId,
                    camera_id = roi.CameraId,
                    room_node_id = roi.RoomNodeId,
                    geometry = System.Text.Json.JsonSerializer.Deserialize<object>(roi.VerticesJson),
                    source_version = "rebuild-2",
                    updated_at = DateTimeOffset.UtcNow
                }, cancellationToken);
                vertices++;
                await graph.UpsertEdgeAsync("covers", ArangoGraphRepository.StableEdgeKey(tenantId, "covers", cameraRef, roiRef), cameraRef, roiRef, Version(tenantId), cancellationToken);
                await graph.UpsertEdgeAsync("located_in", ArangoGraphRepository.StableEdgeKey(tenantId, "located_in", roiRef, roomRef), roiRef, roomRef, Version(tenantId), cancellationToken);
                await graph.UpsertEdgeAsync("covers", ArangoGraphRepository.StableEdgeKey(tenantId, "covers", cameraRef, roomRef), cameraRef, roomRef, Version(tenantId), cancellationToken);
                edges += 3;
            }

            foreach (var topologyEdge in topology)
            {
                var fromRef = GraphKeys.CameraRef(tenantId, topologyEdge.FromCameraId);
                var toRef = GraphKeys.CameraRef(tenantId, topologyEdge.ToCameraId);
                await graph.UpsertEdgeAsync("connects", ArangoGraphRepository.StableEdgeKey(tenantId, topologyEdge.RelationType, fromRef, toRef),
                    fromRef, toRef, new
                    {
                        tenant_id = tenantId,
                        relation_type = topologyEdge.RelationType,
                        weight = topologyEdge.Weight,
                        source_edge_id = topologyEdge.EdgeId,
                        source_version = "rebuild-2",
                        updated_at = DateTimeOffset.UtcNow
                    }, cancellationToken);
                edges++;
            }
        }

        var persons = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            """
            WITH scoped AS (
              SELECT DISTINCT tenant_id,entity_id AS person_id FROM media_track_binding WHERE entity_id IS NOT NULL
              UNION SELECT DISTINCT tenant_id,vid FROM feature_embedding WHERE LEFT(vid,2)<>'T_')
            SELECT scoped.tenant_id AS TenantId,person.v_id AS PersonId,person.first_seen AS FirstSeen,
              person.last_seen AS LastSeen,person.capture_count AS CaptureCount
            FROM scoped JOIN virtual_person person ON person.v_id=scoped.person_id
            """, cancellationToken: cancellationToken))).AsList();
        foreach (var person in persons)
        {
            await graph.UpsertVertexAsync("persons", GraphKeys.Person(person.TenantId, person.PersonId), new
            {
                tenant_id = person.TenantId,
                person_id = person.PersonId,
                first_seen = person.FirstSeen,
                last_seen = person.LastSeen,
                capture_count = person.CaptureCount,
                source_version = "rebuild-2",
                updated_at = DateTimeOffset.UtcNow
            }, cancellationToken);
            vertices++;
        }

        var sources = (await connection.QueryAsync<SourceRow>(new CommandDefinition(
            "SELECT source_id AS SourceId,tenant_id AS TenantId,camera_id AS CameraId,source_code AS SourceCode,source_type AS SourceType,enabled AS Enabled FROM media_source",
            cancellationToken: cancellationToken))).AsList();
        foreach (var source in sources)
        {
            var sourceRef = GraphKeys.SourceRef(source.TenantId, source.SourceId);
            var cameraRef = GraphKeys.CameraRef(source.TenantId, source.CameraId);
            await graph.UpsertVertexAsync("analysis_sources", GraphKeys.Source(source.TenantId, source.SourceId), new
            {
                tenant_id = source.TenantId,
                source_id = source.SourceId,
                source_code = source.SourceCode,
                source_type = source.SourceType,
                enabled = source.Enabled,
                source_version = "rebuild-2",
                updated_at = DateTimeOffset.UtcNow
            }, cancellationToken);
            vertices++;
            await graph.UpsertEdgeAsync("located_in", ArangoGraphRepository.StableEdgeKey(source.TenantId, "source_camera", sourceRef, cameraRef),
                sourceRef, cameraRef, Version(source.TenantId), cancellationToken);
            edges++;
        }

        var maxDynamicRows = Math.Clamp(configuration.GetValue("Graph:Rebuild:MaxDynamicRows", 200000), 1000, 2000000);
        var visits = (await connection.QueryAsync<VisitRow>(new CommandDefinition(
            """
            SELECT scope.tenant_id AS TenantId,event.vid AS PersonId,event.camera_id AS CameraId,
              roi.room_node_id AS RoomId,date_trunc('hour',event.event_time) AS BucketStart,
              COUNT(*) AS Count,MIN(event.event_time) AS FirstSeen,MAX(event.event_time) AS LastSeen
            FROM track_event event
            JOIN (SELECT DISTINCT tenant_id,camera_id FROM media_source) scope ON scope.camera_id=event.camera_id
            LEFT JOIN map_roi roi ON roi.roi_id=event.roi_id
            WHERE LEFT(event.vid,2)<>'T_'
            GROUP BY scope.tenant_id,event.vid,event.camera_id,roi.room_node_id,date_trunc('hour',event.event_time)
            ORDER BY BucketStart DESC LIMIT @Limit
            """, new { Limit = maxDynamicRows }, cancellationToken: cancellationToken))).AsList();
        foreach (var visit in visits)
        {
            var personRef = GraphKeys.PersonRef(visit.TenantId, visit.PersonId);
            var targets = new List<string> { GraphKeys.CameraRef(visit.TenantId, visit.CameraId) };
            if (visit.RoomId.HasValue) targets.Add(GraphKeys.NodeRef("rooms", visit.TenantId, visit.RoomId.Value));
            foreach (var target in targets)
            {
                await graph.UpsertEdgeAsync("visited",
                    ArangoGraphRepository.StableEdgeKey(visit.TenantId, "visited", personRef, target, visit.BucketStart.ToUniversalTime().ToString("O")),
                    personRef, target, DynamicVersion(visit.TenantId, visit.BucketStart, visit.Count, visit.FirstSeen, visit.LastSeen), cancellationToken);
                edges++;
            }
        }

        var coOccurrenceWindow = Math.Clamp(configuration.GetValue("Graph:Projection:CoOccurrenceWindowSeconds", 300), 1, 3600);
        var coOccurrences = (await connection.QueryAsync<CoOccurrenceRow>(new CommandDefinition(
            """
            SELECT scope.tenant_id AS TenantId,LEAST(left_event.vid,right_event.vid) AS LeftPersonId,
              GREATEST(left_event.vid,right_event.vid) AS RightPersonId,
              date_trunc('hour',left_event.event_time) AS BucketStart,COUNT(*) AS Count,
              MIN(LEAST(left_event.event_time,right_event.event_time)) AS FirstSeen,
              MAX(GREATEST(left_event.event_time,right_event.event_time)) AS LastSeen
            FROM track_event left_event
            JOIN track_event right_event ON right_event.event_id>left_event.event_id
              AND right_event.vid<>left_event.vid AND right_event.camera_id=left_event.camera_id
              AND right_event.roi_id IS NOT DISTINCT FROM left_event.roi_id
              AND ABS(EXTRACT(EPOCH FROM (right_event.event_time-left_event.event_time)))<=@WindowSeconds
            JOIN (SELECT DISTINCT tenant_id,camera_id FROM media_source) scope ON scope.camera_id=left_event.camera_id
            WHERE LEFT(left_event.vid,2)<>'T_' AND LEFT(right_event.vid,2)<>'T_'
            GROUP BY scope.tenant_id,LEAST(left_event.vid,right_event.vid),GREATEST(left_event.vid,right_event.vid),
              date_trunc('hour',left_event.event_time)
            ORDER BY BucketStart DESC LIMIT @Limit
            """, new { WindowSeconds = coOccurrenceWindow, Limit = maxDynamicRows }, cancellationToken: cancellationToken))).AsList();
        foreach (var relation in coOccurrences)
        {
            var fromRef = GraphKeys.PersonRef(relation.TenantId, relation.LeftPersonId);
            var toRef = GraphKeys.PersonRef(relation.TenantId, relation.RightPersonId);
            await graph.UpsertEdgeAsync("co_occurs",
                ArangoGraphRepository.StableEdgeKey(relation.TenantId, "co_occurs", fromRef, toRef, relation.BucketStart.ToUniversalTime().ToString("O")),
                fromRef, toRef, DynamicVersion(relation.TenantId, relation.BucketStart, relation.Count, relation.FirstSeen, relation.LastSeen), cancellationToken);
            edges++;
        }

        var transitionGap = Math.Clamp(configuration.GetValue("Graph:Projection:TransitionMaxGapSeconds", 1800), 1, 86400);
        var transitions = (await connection.QueryAsync<TransitionRow>(new CommandDefinition(
            """
            WITH scoped AS (
              SELECT source.tenant_id,event.vid,event.camera_id,event.event_time,event.event_id
              FROM track_event event
              JOIN (SELECT DISTINCT tenant_id,camera_id FROM media_source) source ON source.camera_id=event.camera_id
              WHERE LEFT(event.vid,2)<>'T_'),
            ordered AS (
              SELECT *,LAG(camera_id) OVER(PARTITION BY tenant_id,vid ORDER BY event_time,event_id) AS previous_camera_id,
                LAG(event_time) OVER(PARTITION BY tenant_id,vid ORDER BY event_time,event_id) AS previous_time
              FROM scoped)
            SELECT tenant_id AS TenantId,previous_camera_id AS FromCameraId,camera_id AS ToCameraId,
              date_trunc('hour',event_time) AS BucketStart,COUNT(*) AS Count,
              MIN(previous_time) AS FirstSeen,MAX(event_time) AS LastSeen
            FROM ordered WHERE previous_camera_id IS NOT NULL AND previous_camera_id<>camera_id
              AND EXTRACT(EPOCH FROM (event_time-previous_time)) BETWEEN 0 AND @MaxGapSeconds
            GROUP BY tenant_id,previous_camera_id,camera_id,date_trunc('hour',event_time)
            ORDER BY BucketStart DESC LIMIT @Limit
            """, new { MaxGapSeconds = transitionGap, Limit = maxDynamicRows }, cancellationToken: cancellationToken))).AsList();
        foreach (var transition in transitions)
        {
            var fromRef = GraphKeys.CameraRef(transition.TenantId, transition.FromCameraId);
            var toRef = GraphKeys.CameraRef(transition.TenantId, transition.ToCameraId);
            await graph.UpsertEdgeAsync("transition",
                ArangoGraphRepository.StableEdgeKey(transition.TenantId, "transition", fromRef, toRef, transition.BucketStart.ToUniversalTime().ToString("O")),
                fromRef, toRef, DynamicVersion(transition.TenantId, transition.BucketStart, transition.Count, transition.FirstSeen, transition.LastSeen), cancellationToken);
            edges++;
        }

        logger.LogInformation("Graph rebuild prepared {Vertices} vertices and {Edges} edges.", vertices, edges);
        return (vertices, edges);
    }

    private static object Version(long tenantId) => new { tenant_id = tenantId, source_version = "rebuild-2", updated_at = DateTimeOffset.UtcNow };
    private static object DynamicVersion(long tenantId, DateTime bucketStart, long count, DateTime firstSeen, DateTime lastSeen) => new
    {
        tenant_id = tenantId,
        bucket_start = bucketStart,
        count,
        first_seen = firstSeen,
        last_seen = lastSeen,
        source_version = "rebuild-2",
        updated_at = DateTimeOffset.UtcNow
    };

    private static string NodeCollection(string levelType) => levelType.Trim().ToLowerInvariant() switch
    {
        "campus" or "园区" => "campuses",
        "building" or "楼栋" => "buildings",
        "floor" or "楼层" => "floors",
        "room" or "房间" => "rooms",
        _ => "rooms"
    };

    private sealed record CampusNode(long NodeId, long? ParentId, string LevelType, string NodeName);
    private sealed record FloorRow(long FloorId, long NodeId, string FilePath, decimal ScaleRatio);
    private sealed record CameraRow(long CameraId, long FloorId, long DeviceId, int ChannelNo, decimal PosX, decimal PosY, string? DeviceName, string? DeviceStatus);
    private sealed record RoiRow(long RoiId, long CameraId, long RoomNodeId, string VerticesJson);
    private sealed record TopologyRow(long EdgeId, long FromCameraId, long ToCameraId, string RelationType, decimal Weight);
    private sealed record PersonRow(long TenantId, string PersonId, DateTime FirstSeen, DateTime LastSeen, int CaptureCount);
    private sealed record SourceRow(long SourceId, long TenantId, long CameraId, string SourceCode, string SourceType, bool Enabled);
    private sealed record VisitRow(long TenantId, string PersonId, long CameraId, long? RoomId, DateTime BucketStart, long Count, DateTime FirstSeen, DateTime LastSeen);
    private sealed record CoOccurrenceRow(long TenantId, string LeftPersonId, string RightPersonId, DateTime BucketStart, long Count, DateTime FirstSeen, DateTime LastSeen);
    private sealed record TransitionRow(long TenantId, long FromCameraId, long ToCameraId, DateTime BucketStart, long Count, DateTime FirstSeen, DateTime LastSeen);
}

internal sealed class GraphRebuildHostedService(
    GraphProjectionRepository repository,
    GraphRebuildService rebuildService,
    BackgroundWorkerHeartbeat heartbeat,
    ILogger<GraphRebuildHostedService> logger) : BackgroundService
{
    private const string WorkerName = "graph-rebuild";
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rebuildId = await repository.ClaimRebuildAsync(stoppingToken);
                if (!rebuildId.HasValue)
                {
                    await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                try
                {
                    var counts = await rebuildService.RebuildAsync(stoppingToken);
                    await repository.CompleteRebuildAsync(rebuildId.Value, counts.Vertices, counts.Edges, null, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Graph rebuild failed. rebuildId={RebuildId}", rebuildId.Value);
                    await repository.CompleteRebuildAsync(rebuildId.Value, 0, 0, ex.Message, stoppingToken);
                }
                await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Graph rebuild worker iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _instanceId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
