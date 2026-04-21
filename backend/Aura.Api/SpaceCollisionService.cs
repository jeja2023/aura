using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Models;
using Aura.Api.Ops;
using Microsoft.AspNetCore.Http;

internal sealed class SpaceCollisionService
{
    private readonly AppStore _store;
    private readonly CaptureRepository _captureRepository;
    private readonly EventDispatchService _eventDispatchService;

    public SpaceCollisionService(AppStore store, CaptureRepository captureRepository, EventDispatchService eventDispatchService)
    {
        _store = store;
        _captureRepository = captureRepository;
        _eventDispatchService = eventDispatchService;
    }

    public async Task<IResult> CheckCollisionAsync(SpaceCollisionReq req)
    {
        var roisDb = await _captureRepository.GetRoisAsync();
        var rois = roisDb.Count > 0
            ? roisDb.Select(x => new RoiEntity(x.RoiId, x.CameraId, x.RoomNodeId, x.VerticesJson, x.CreatedAt)).ToList()
            : _store.Rois.ToList();
        var matched = ResolveCollision(rois, req.CameraId, req.PosX, req.PosY);
        if (matched.Count == 0)
        {
            return Results.Ok(new { code = 0, msg = "未命中任何防区", data = new { hit = false, roomNodeIds = Array.Empty<long>() } });
        }

        var eventTime = req.EventTime ?? DateTimeOffset.Now;
        var vid = string.IsNullOrWhiteSpace(req.Vid) ? $"V_TMP_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}" : req.Vid.Trim();
        var events = new List<TrackEventEntity>();
        foreach (var item in matched)
        {
            var dbId = await _captureRepository.InsertTrackEventAsync(vid, req.CameraId, item.RoiId, eventTime);
            var local = new TrackEventEntity(dbId ?? Interlocked.Increment(ref _store.TrackEventSeed), vid, req.CameraId, item.RoiId, eventTime);
            if (!dbId.HasValue)
            {
                _store.TrackEvents.Add(local);
            }
            events.Add(local);
        }

        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: "空间引擎",
            Action: "空间碰撞判定",
            Detail: $"camera={req.CameraId}, x={req.PosX}, y={req.PosY}, hit={matched.Count}",
            CreatedAt: DateTimeOffset.Now));
        await _eventDispatchService.BroadcastRoleEventAsync("track.event", new { vid, cameraId = req.CameraId, roiCount = matched.Count, eventTime });

        return Results.Ok(new
        {
            code = 0,
            msg = "碰撞判定完成",
            data = new
            {
                hit = true,
                vid,
                roiIds = matched.Select(x => x.RoiId).Distinct(),
                roomNodeIds = matched.Select(x => x.RoomNodeId).Distinct(),
                eventTime,
                events
            }
        });
    }

    private static List<RoiEntity> ResolveCollision(List<RoiEntity> rois, long cameraId, decimal posX, decimal posY)
    {
        var x = (double)posX;
        var y = (double)posY;
        var result = new List<RoiEntity>();
        foreach (var roi in rois.Where(r => r.CameraId == cameraId))
        {
            var points = ParsePoints(roi.VerticesJson);
            if (points.Count < 3)
            {
                continue;
            }
            if (IsPointInPolygon(points, x, y))
            {
                result.Add(roi);
            }
        }
        return result;
    }

    private static List<PointVm> ParsePoints(string verticesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(verticesJson) ? "[]" : verticesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var points = new List<PointVm>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var px = item.TryGetProperty("x", out var xEl) ? xEl.GetDouble() : 0d;
                var py = item.TryGetProperty("y", out var yEl) ? yEl.GetDouble() : 0d;
                points.Add(new PointVm(px, py));
            }
            return points;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPointInPolygon(List<PointVm> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var xi = polygon[i].X;
            var yi = polygon[i].Y;
            var xj = polygon[j].X;
            var yj = polygon[j].Y;
            var intersect = ((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / ((yj - yi) + double.Epsilon) + xi);
            if (intersect)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
