using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsCampusFloor
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var campusResources = ctx.CampusResources;
        var audit = ctx.Audit;
        var store = ctx.Store;
        var allow = ctx.AllowInMemoryFallback;

        var campusGroup = app.MapGroup("/api/campus");
        campusGroup.MapGet("/tree", async () =>
        {
            var rows = await campusResources.GetCampusNodesAsync();
            if (rows.Count > 0)
            {
                var vmMap = rows.Select(x => new CampusNodeVm(x.NodeId, x.ParentId, x.LevelType, x.NodeName, [])).ToDictionary(x => x.NodeId);
                var roots = new List<CampusNodeVm>();
                foreach (var node in vmMap.Values)
                {
                    if (node.ParentId.HasValue && vmMap.TryGetValue(node.ParentId.Value, out var parent)) parent.Children.Add(node);
                    else roots.Add(node);
                }

                return Results.Ok(new { code = 0, msg = "查询成功", data = roots });
            }

            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<CampusNodeVm>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.CampusNodes.Where(x => x.ParentId == null) });
        }).RequireAuthorization("楼栋管理员");

        campusGroup.MapPost("/create", async (CampusCreateReq req) =>
        {
            var dbId = await campusResources.InsertCampusNodeAsync(req.ParentId, req.LevelType, req.NodeName);
            if (dbId.HasValue)
            {
                await audit.InsertOperationAsync("楼栋管理员", "区域创建", $"名称={req.NodeName}");
                return Results.Ok(new { code = 0, msg = "创建成功", data = new { nodeId = dbId.Value, req.ParentId, req.LevelType, req.NodeName } });
            }

            if (!allow) return AuraApiResults.ServiceUnavailable("数据库写入失败，无法创建区域", 50301);
            var entity = new CampusNodeEntity(Interlocked.Increment(ref store.CampusSeed), req.ParentId, req.LevelType, req.NodeName);
            store.CampusNodes.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "区域创建", $"名称={req.NodeName}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        campusGroup.MapPost("/update/{nodeId:long}", async (long nodeId, CampusUpdateReq req) =>
        {
            var ok = await campusResources.UpdateCampusNodeAsync(nodeId, req.NodeName);
            if (ok) return Results.Ok(new { code = 0, msg = "更新成功" });
            if (!allow) return AuraApiResults.NotFound("区域不存在", 40403);
            var entity = store.CampusNodes.FirstOrDefault(x => x.NodeId == nodeId);
            if (entity is null) return AuraApiResults.NotFound("区域不存在", 40403);
            store.CampusNodes.Remove(entity);
            store.CampusNodes.Add(entity with { NodeName = req.NodeName });
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "区域更新", $"名称={req.NodeName}");
            return Results.Ok(new { code = 0, msg = "更新成功" });
        }).RequireAuthorization("楼栋管理员");

        campusGroup.MapPost("/delete/{nodeId:long}", async (long nodeId) =>
        {
            var ok = await campusResources.DeleteCampusNodeAsync(nodeId);
            if (ok) return Results.Ok(new { code = 0, msg = "删除成功" });
            if (!allow) return AuraApiResults.NotFound("区域不存在", 40403);
            var entity = store.CampusNodes.FirstOrDefault(x => x.NodeId == nodeId);
            if (entity is null) return AuraApiResults.NotFound("区域不存在", 40403);
            store.CampusNodes.Remove(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "区域删除", $"ID={nodeId}");
            return Results.Ok(new { code = 0, msg = "删除成功" });
        }).RequireAuthorization("楼栋管理员");

        var floorGroup = app.MapGroup("/api/floor");
        floorGroup.MapGet("/list", async () =>
        {
            var rows = await campusResources.GetFloorsAsync();
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbFloor>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Floors.OrderByDescending(x => x.FloorId) });
        }).RequireAuthorization("楼栋管理员");

        floorGroup.MapPost("/create", async (FloorCreateReq req) =>
        {
            var dbId = await campusResources.InsertFloorAsync(req.NodeId, req.FilePath, req.ScaleRatio);
            if (dbId.HasValue)
            {
                await audit.InsertOperationAsync("楼栋管理员", "楼层创建", $"节点ID={req.NodeId}");
                return Results.Ok(new { code = 0, msg = "创建成功", data = new { floorId = dbId.Value, req.NodeId, req.FilePath, req.ScaleRatio } });
            }

            if (!allow) return AuraApiResults.ServiceUnavailable("数据库写入失败，无法创建楼层", 50301);
            var entity = new FloorEntity(Interlocked.Increment(ref store.FloorSeed), req.NodeId, req.FilePath, req.ScaleRatio);
            store.Floors.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "楼层创建", $"节点ID={req.NodeId}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        floorGroup.MapPost("/upload", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return AuraApiResults.BadRequest("请使用表单上传", 40031);
            var form = await request.ReadFormAsync();
            var file = form.Files["file"];
            if (file is null || file.Length == 0) return AuraApiResults.BadRequest("未找到上传文件", 40032);
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".webp" }.All(x => x != ext)) return AuraApiResults.BadRequest("仅支持 png/jpg/jpeg/webp", 40033);

            var env = request.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
            var storageRoot = ProjectPaths.ResolveStorageRoot(env);
            var folder = Path.Combine(storageRoot, "uploads", "floors");
            Directory.CreateDirectory(folder);
            var safeName = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{ext}";
            var localPath = Path.Combine(folder, safeName);
            await using (var fs = File.Create(localPath)) await file.CopyToAsync(fs);
            var filePath = $"/storage/uploads/floors/{safeName}";
            await audit.InsertOperationAsync("楼栋管理员", "楼层图上传", $"文件={safeName}");
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "楼层图上传", $"文件={safeName}");
            return Results.Ok(new { code = 0, msg = "上传成功", data = new { filePath, originalName = file.FileName, size = file.Length } });
        }).RequireAuthorization("楼栋管理员");
    }
}
