using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Microsoft.AspNetCore.Http;

internal sealed class ResourceManagementService
{
    private readonly AppStore _store;
    private readonly CampusResourceRepository _campusResourceRepository;
    private readonly CaptureRepository _captureRepository;
    private readonly AuditRepository _auditRepository;
    private readonly string _storageRoot;

    public ResourceManagementService(
        AppStore store,
        CampusResourceRepository campusResourceRepository,
        CaptureRepository captureRepository,
        AuditRepository auditRepository,
        string storageRoot)
    {
        _store = store;
        _campusResourceRepository = campusResourceRepository;
        _captureRepository = captureRepository;
        _auditRepository = auditRepository;
        _storageRoot = storageRoot;
    }

    public async Task<IResult> GetCampusTreeAsync()
    {
        var nodes = await _campusResourceRepository.GetCampusNodesAsync();
        if (nodes.Count > 0)
        {
            var dict = nodes.ToDictionary(x => x.NodeId, x => new CampusNodeVm(x.NodeId, x.ParentId, x.LevelType, x.NodeName, []));
            foreach (var item in dict.Values)
            {
                if (item.ParentId.HasValue && dict.TryGetValue(item.ParentId.Value, out var parent))
                {
                    parent.Children.Add(item);
                }
            }
            var roots = dict.Values.Where(x => !x.ParentId.HasValue || !dict.ContainsKey(x.ParentId.Value)).ToList();
            return Results.Ok(new { code = 0, msg = "查询成功", data = roots });
        }

        return Results.Ok(new { code = 0, msg = "查询成功", data = _store.CampusNodes.OrderBy(x => x.NodeId) });
    }

    public async Task<IResult> CreateCampusAsync(CampusCreateReq req)
    {
        if (string.IsNullOrWhiteSpace(req.LevelType) || string.IsNullOrWhiteSpace(req.NodeName))
        {
            return AuraApiResults.BadRequest("层级类型和节点名称不能为空", 40021);
        }

        var dbId = await _campusResourceRepository.InsertCampusNodeAsync(req.ParentId, req.LevelType, req.NodeName);
        if (dbId.HasValue)
        {
            await _auditRepository.InsertOperationAsync("楼栋管理员", "资源节点创建", $"节点={req.NodeName}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { nodeId = dbId.Value, req.ParentId, req.LevelType, req.NodeName } });
        }

        var entity = new CampusNodeEntity(Interlocked.Increment(ref _store.CampusSeed), req.ParentId, req.LevelType, req.NodeName);
        _store.CampusNodes.Add(entity);
        AddOperationLog("楼栋管理员", "资源节点创建", $"节点={req.NodeName}");
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> UpdateCampusAsync(long nodeId, CampusUpdateReq req)
    {
        var ok = await _campusResourceRepository.UpdateCampusNodeAsync(nodeId, req.NodeName);
        if (ok)
        {
            await _auditRepository.InsertOperationAsync("楼栋管理员", "资源节点更新", $"节点ID={nodeId}");
            return Results.Ok(new { code = 0, msg = "更新成功" });
        }

        var entity = _store.CampusNodes.FirstOrDefault(x => x.NodeId == nodeId);
        if (entity is null)
        {
            return AuraApiResults.NotFound("节点不存在", 40421);
        }

        var updated = entity with { NodeName = req.NodeName };
        _store.CampusNodes.Remove(entity);
        _store.CampusNodes.Add(updated);
        AddOperationLog("楼栋管理员", "资源节点更新", $"节点ID={nodeId}");
        return Results.Ok(new { code = 0, msg = "更新成功", data = updated });
    }

    public async Task<IResult> DeleteCampusAsync(long nodeId)
    {
        var ok = await _campusResourceRepository.DeleteCampusNodeAsync(nodeId);
        if (ok)
        {
            await _auditRepository.InsertOperationAsync("楼栋管理员", "资源节点删除", $"节点ID={nodeId}");
            return Results.Ok(new { code = 0, msg = "删除成功" });
        }

        _store.CampusNodes.RemoveAll(x => x.NodeId == nodeId || x.ParentId == nodeId);
        AddOperationLog("楼栋管理员", "资源节点删除", $"节点ID={nodeId}");
        return Results.Ok(new { code = 0, msg = "删除成功" });
    }

    public async Task<IResult> GetFloorsAsync()
    {
        var rows = await _campusResourceRepository.GetFloorsAsync();
        return rows.Count > 0
            ? Results.Ok(new { code = 0, msg = "查询成功", data = rows })
            : Results.Ok(new { code = 0, msg = "查询成功", data = _store.Floors.OrderByDescending(x => x.FloorId) });
    }

    public async Task<IResult> CreateFloorAsync(FloorCreateReq req)
    {
        var dbId = await _campusResourceRepository.InsertFloorAsync(req.NodeId, req.FilePath, req.ScaleRatio);
        if (dbId.HasValue)
        {
            await _auditRepository.InsertOperationAsync("楼栋管理员", "楼层图创建", $"节点ID={req.NodeId}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { floorId = dbId.Value, req.NodeId, req.FilePath, req.ScaleRatio } });
        }

        var entity = new FloorEntity(Interlocked.Increment(ref _store.FloorSeed), req.NodeId, req.FilePath, req.ScaleRatio);
        _store.Floors.Add(entity);
        AddOperationLog("楼栋管理员", "楼层图创建", $"节点ID={req.NodeId}");
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> UploadFloorAsync(HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return AuraApiResults.BadRequest("请使用表单上传", 40031);
        }

        var form = await request.ReadFormAsync();
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return AuraApiResults.BadRequest("未找到上传文件", 40032);
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allow = new[] { ".png", ".jpg", ".jpeg", ".webp" };
        if (!allow.Contains(ext))
        {
            return AuraApiResults.BadRequest("仅支持 png/jpg/jpeg/webp", 40033);
        }

        var folder = Path.Combine(_storageRoot, "uploads", "floors");
        Directory.CreateDirectory(folder);
        var safeName = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{ext}";
        var localPath = Path.Combine(folder, safeName);
        await using (var fs = File.Create(localPath))
        {
            await file.CopyToAsync(fs);
        }

        var filePath = $"/storage/uploads/floors/{safeName}";
        await _auditRepository.InsertOperationAsync("楼栋管理员", "楼层图上传", $"文件={safeName}");
        AddOperationLog("楼栋管理员", "楼层图上传", $"文件={safeName}");
        return Results.Ok(new { code = 0, msg = "上传成功", data = new { filePath, originalName = file.FileName, size = file.Length } });
    }

    public async Task<IResult> GetCamerasAsync()
    {
        var rows = await _campusResourceRepository.GetCamerasAsync();
        return rows.Count > 0
            ? Results.Ok(new { code = 0, msg = "查询成功", data = rows })
            : Results.Ok(new { code = 0, msg = "查询成功", data = _store.Cameras.OrderByDescending(x => x.CameraId) });
    }

    public async Task<IResult> CreateCameraAsync(CameraCreateReq req)
    {
        var dbId = await _campusResourceRepository.InsertCameraAsync(req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
        if (dbId.HasValue)
        {
            await _auditRepository.InsertOperationAsync("楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = new { cameraId = dbId.Value, req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY } });
        }

        var entity = new CameraEntity(Interlocked.Increment(ref _store.CameraSeed), req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
        _store.Cameras.Add(entity);
        AddOperationLog("楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
        return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
    }

    public async Task<IResult> GetRoisAsync()
    {
        var rows = await _captureRepository.GetRoisAsync();
        if (rows.Count > 0)
        {
            var mapped = rows.Select(x => new RoiEntity(x.RoiId, x.CameraId, x.RoomNodeId, x.VerticesJson, x.CreatedAt));
            return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
        }

        return Results.Ok(new { code = 0, msg = "查询成功", data = _store.Rois.OrderByDescending(x => x.RoiId) });
    }

    public async Task<IResult> SaveRoiAsync(RoiReq req)
    {
        var entity = new RoiEntity(Interlocked.Increment(ref _store.RoiSeed), req.CameraId, req.RoomNodeId, req.VerticesJson, DateTimeOffset.Now);
        var dbId = await _captureRepository.InsertRoiAsync(req.CameraId, req.RoomNodeId, req.VerticesJson);
        var saved = dbId.HasValue ? entity with { RoiId = dbId.Value } : entity;
        if (!dbId.HasValue)
        {
            _store.Rois.Add(saved);
        }
        AddOperationLog("楼栋管理员", "防区保存", $"相机={req.CameraId}, 房间={req.RoomNodeId}");
        return Results.Ok(new { code = 0, msg = "ROI保存成功", data = saved });
    }

    private void AddOperationLog(string operatorName, string action, string detail)
    {
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: operatorName,
            Action: action,
            Detail: detail,
            CreatedAt: DateTimeOffset.Now));
    }
}
