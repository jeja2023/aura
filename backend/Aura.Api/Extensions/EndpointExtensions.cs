using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Aura.Api.Models;
using Aura.Api.Data;
using Aura.Api.Cache;
using Aura.Api.Ops;
using Aura.Api.Ai;
using Aura.Api.Internal;
using Aura.Api.Hubs;
using Aura.Api.Capture;
using Aura.Api.Capture.Adapters;
using Aura.Api.Clustering;
using Aura.Api.Export;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Extensions;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapAuraEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration, bool isDev)
    {
        var jwtKey = configuration["Jwt:Key"] ?? "aura-dev-jwt-key-please-change";
        var jwtIssuer = configuration["Jwt:Issuer"] ?? "Aura.Api";
        var jwtAudience = configuration["Jwt:Audience"] ?? "Aura.Client";
        var jwtExpireMinutes = int.TryParse(configuration["Jwt:ExpireMinutes"], out var m) ? m : 480;
        var globalHmacSecret = configuration["Security:HmacSecret"] ?? "demo-hmac-secret";
        var captureIpWhitelist = configuration.GetSection("Security:CaptureIpWhitelist").Get<string[]>();
        
        const int MaxImageBase64Chars = 5_000_000;
        const int MaxMetadataJsonChars = 200_000;
        const long MaxCaptureRequestBytes = 12L * 1024 * 1024;

        var db = app.ServiceProvider.GetRequiredService<PgSqlStore>();
        var cache = app.ServiceProvider.GetRequiredService<RedisCacheService>();
        var alertNotifier = app.ServiceProvider.GetRequiredService<IAlertNotifier>();
        var store = app.ServiceProvider.GetRequiredService<AppStore>();
        var ai = app.ServiceProvider.GetRequiredService<AiClient>();
        var readinessLogger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("OpsReadiness");

        app.MapHub<EventHub>("/hubs/events");

        app.MapGet("/", () => Results.Redirect("/index/"));
        app.MapGet("/api/health", () => Results.Ok(new { code = 0, msg = "寓瞳中枢服务运行正常", time = DateTimeOffset.Now }));

        app.MapGet("/api/ops/readiness", async () =>
        {
            var now = DateTimeOffset.Now;
            var alertWindowMinutes = int.TryParse(configuration["Ops:Alert:HealthFailIfRecentFailureMinutes"], out var win) ? win : 10;
            
            // Deep check AI service
            var aiReady = false;
            var aiModelLoaded = false;
            try {
                var aiHealth = await ai.GetHealthAsync();
                aiReady = aiHealth != null;
                if (aiHealth.HasValue)
                {
                    aiModelLoaded = aiHealth.Value.TryGetProperty("model_loaded", out var p) && p.GetBoolean();
                }
            } catch (Exception ex) {
                readinessLogger.LogWarning(ex, "就绪检查发现 AI 服务连接异常");
            }

            var alertStats = alertNotifier.GetStats();
            var lastFailureAt = alertStats.LastFailureAt;
            var windowStart = now.AddMinutes(-alertWindowMinutes);
            var alertNotifyRecentFailure = alertWindowMinutes > 0
                && lastFailureAt.HasValue
                && lastFailureAt.GetValueOrDefault() >= windowStart;

            var jwtSecret = configuration["Jwt:Key"] ?? "";
            var hmacSecret = configuration["Security:HmacSecret"] ?? "";
            
            var checks = new Dictionary<string, bool>
            {
                ["jwt"] = !string.IsNullOrWhiteSpace(jwtSecret) && !jwtSecret.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase),
                ["hmac"] = !string.IsNullOrWhiteSpace(hmacSecret) && !hmacSecret.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase),
                ["pgsql"] = true,
                ["redis"] = cache.Enabled,
                ["ai_service"] = aiReady,
                ["ai_model"] = aiModelLoaded,
                ["alertNotify"] = !alertNotifyRecentFailure
            };
            var ready = checks.Values.All(v => v);
            return Results.Ok(new { code = 0, msg = ready ? "就绪检查通过" : "就绪检查未通过", data = new { environment = isDev ? "开发环境" : "生产环境", ready, checks }, time = now });
        }).RequireAuthorization("超级管理员");

        app.MapPost("/api/ops/alert-notify-test", async (OpsAlertNotifyTestReq req) =>
        {
            var alertType = req.AlertType ?? "运维自检";
            await alertNotifier.NotifyAsync(new AlertNotifyMessage(alertType, req.Detail ?? "自检消息", "ops.test", DateTimeOffset.Now));
            await db.InsertOperationAsync("系统管理员", "告警通知自检", $"类型={alertType}");
            return Results.Ok(new { code = 0, msg = "已发送" });
        }).RequireAuthorization("超级管理员");

        app.MapGet("/api/ops/alert-notify-stats", () => Results.Ok(new { code = 0, msg = "获取统计成功", data = alertNotifier.GetStats(), time = DateTimeOffset.Now })).RequireAuthorization("超级管理员");

        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", async (HttpContext http, LoginReq req, IdentityAdminService svc) => await svc.LoginAsync(http, req));
        auth.MapPost("/logout", (HttpContext http, IdentityAdminService svc) => svc.Logout(http));
        auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { code = 0, msg = "查询成功", data = new { userName = user.Identity?.Name, role = user.FindFirst(ClaimTypes.Role)?.Value } })).RequireAuthorization();

        var roleGroup = app.MapGroup("/api/role");
        roleGroup.MapGet("/list", async (IdentityAdminService svc) => await svc.GetRolesAsync()).RequireAuthorization("超级管理员");
        roleGroup.MapPost("/create", async (RoleCreateReq req, IdentityAdminService svc) => await svc.CreateRoleAsync(req)).RequireAuthorization("超级管理员");

        var userGroup = app.MapGroup("/api/user");
        userGroup.MapGet("/list", async (IdentityAdminService svc) => await svc.GetUsersAsync()).RequireAuthorization("超级管理员");
        userGroup.MapPost("/create", async (UserCreateReq req, IdentityAdminService svc) => await svc.CreateUserAsync(req)).RequireAuthorization("超级管理员");
        userGroup.MapPost("/status/{userId:long}", async (long userId, UserStatusReq req, IdentityAdminService svc) => await svc.UpdateUserStatusAsync(userId, req)).RequireAuthorization("超级管理员");

        var campusGroup = app.MapGroup("/api/campus");
        campusGroup.MapGet("/tree", async () =>
        {
            var rows = await db.GetCampusNodesAsync();
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
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.CampusNodes.Where(x => x.ParentId == null) });
        }).RequireAuthorization("楼栋管理员");
        campusGroup.MapPost("/create", async (CampusCreateReq req) =>
        {
            var dbId = await db.InsertCampusNodeAsync(req.ParentId, req.LevelType, req.NodeName);
            if (dbId.HasValue)
            {
                await db.InsertOperationAsync("楼栋管理员", "区域创建", $"名称={req.NodeName}");
                return Results.Ok(new { code = 0, msg = "创建成功", data = new { nodeId = dbId.Value, req.ParentId, req.LevelType, req.NodeName } });
            }
            var entity = new CampusNodeEntity(Interlocked.Increment(ref store.CampusSeed), req.ParentId, req.LevelType, req.NodeName);
            store.CampusNodes.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "区域创建", $"名称={req.NodeName}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");
        campusGroup.MapPost("/update/{nodeId:long}", async (long nodeId, CampusUpdateReq req) =>
        {
            var ok = await db.UpdateCampusNodeAsync(nodeId, req.NodeName);
            if (ok) return Results.Ok(new { code = 0, msg = "更新成功" });
            var entity = store.CampusNodes.FirstOrDefault(x => x.NodeId == nodeId);
            if (entity is null) return Results.NotFound(new { code = 40403, msg = "区域不存在" });
            store.CampusNodes.Remove(entity);
            store.CampusNodes.Add(entity with { NodeName = req.NodeName });
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "区域更新", $"名称={req.NodeName}");
            return Results.Ok(new { code = 0, msg = "更新成功" });
        }).RequireAuthorization("楼栋管理员");
        campusGroup.MapPost("/delete/{nodeId:long}", async (long nodeId) =>
        {
            var ok = await db.DeleteCampusNodeAsync(nodeId);
            if (ok) return Results.Ok(new { code = 0, msg = "删除成功" });
            var entity = store.CampusNodes.FirstOrDefault(x => x.NodeId == nodeId);
            if (entity is null) return Results.NotFound(new { code = 40403, msg = "区域不存在" });
            store.CampusNodes.Remove(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "区域删除", $"ID={nodeId}");
            return Results.Ok(new { code = 0, msg = "删除成功" });
        }).RequireAuthorization("楼栋管理员");

        var floorGroup = app.MapGroup("/api/floor");
        floorGroup.MapGet("/list", async () =>
        {
            var rows = await db.GetFloorsAsync();
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Floors.OrderByDescending(x => x.FloorId) });
        }).RequireAuthorization("楼栋管理员");
        floorGroup.MapPost("/create", async (FloorCreateReq req) =>
        {
            var dbId = await db.InsertFloorAsync(req.NodeId, req.FilePath, req.ScaleRatio);
            if (dbId.HasValue)
            {
                await db.InsertOperationAsync("楼栋管理员", "楼层创建", $"节点ID={req.NodeId}");
                return Results.Ok(new { code = 0, msg = "创建成功", data = new { floorId = dbId.Value, req.NodeId, req.FilePath, req.ScaleRatio } });
            }
            var entity = new FloorEntity(Interlocked.Increment(ref store.FloorSeed), req.NodeId, req.FilePath, req.ScaleRatio);
            store.Floors.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "楼层创建", $"节点ID={req.NodeId}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");
        floorGroup.MapPost("/upload", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { code = 40031, msg = "请使用表单上传" });
            var form = await request.ReadFormAsync();
            var file = form.Files["file"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { code = 40032, msg = "未找到上传文件" });
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".webp" }.All(x => x != ext)) return Results.BadRequest(new { code = 40033, msg = "仅支持 png/jpg/jpeg/webp" });
            
            var projectRoot = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.FullName ?? AppContext.BaseDirectory;
            var storageRoot = Path.Combine(projectRoot, "storage");
            var folder = Path.Combine(storageRoot, "uploads", "floors");
            Directory.CreateDirectory(folder);
            var safeName = $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}{ext}";
            var localPath = Path.Combine(folder, safeName);
            await using (var fs = File.Create(localPath)) await file.CopyToAsync(fs);
            var filePath = $"/storage/uploads/floors/{safeName}";
            await db.InsertOperationAsync("楼栋管理员", "楼层图上传", $"文件={safeName}");
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "楼层图上传", $"文件={safeName}");
            return Results.Ok(new { code = 0, msg = "上传成功", data = new { filePath, originalName = file.FileName, size = file.Length } });
        }).RequireAuthorization("楼栋管理员");

        var cameraGroup = app.MapGroup("/api/camera");
        cameraGroup.MapGet("/list", async () =>
        {
            var rows = await db.GetCamerasAsync();
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Cameras.OrderByDescending(x => x.CameraId) });
        }).RequireAuthorization("楼栋管理员");
        cameraGroup.MapPost("/create", async (CameraCreateReq req) =>
        {
            var dbId = await db.InsertCameraAsync(req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
            if (dbId.HasValue)
            {
                await db.InsertOperationAsync("楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
                return Results.Ok(new { code = 0, msg = "创建成功", data = new { cameraId = dbId.Value, req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY } });
            }
            var entity = new CameraEntity(Interlocked.Increment(ref store.CameraSeed), req.FloorId, req.DeviceId, req.ChannelNo, req.PosX, req.PosY);
            store.Cameras.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "摄像头布点创建", $"楼层ID={req.FloorId}, 设备ID={req.DeviceId}");
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        var deviceGroup = app.MapGroup("/api/device");
        deviceGroup.MapGet("/list", async (DeviceManagementService svc) => await svc.GetDevicesAsync()).RequireAuthorization("楼栋管理员");
        deviceGroup.MapPost("/register", async (DeviceRegisterReq req, DeviceManagementService svc) => await svc.RegisterDeviceAsync(req)).RequireAuthorization("超级管理员");
        deviceGroup.MapPost("/ping/{deviceId:long}", (long deviceId, DeviceManagementService svc) => svc.PingDevice(deviceId)).RequireAuthorization("楼栋管理员");

        var captureGroup = app.MapGroup("/api/capture");
        captureGroup.MapPost("/push", async (HttpContext http, CaptureProcessingService svc) =>
        {
            var reqJson = await http.Request.ReadFromJsonAsync<JsonElement>();
            var normalized = new HikvisionIsapiAdapter().Normalize(reqJson);
            var v = await AuraHelpers.ValidateCaptureRequest(http.Request, normalized, db, isDev, globalHmacSecret, captureIpWhitelist, MaxCaptureRequestBytes, MaxImageBase64Chars, MaxMetadataJsonChars, cache);
            return v ?? await svc.ProcessAsync(normalized, "海康ISAPI抓拍");
        });
        captureGroup.MapPost("/sdk", async (HttpContext http, CaptureProcessingService svc) =>
        {
            var reqJson = await http.Request.ReadFromJsonAsync<JsonElement>();
            var normalized = new CppSdkAdapter().Normalize(reqJson);
            var v = await AuraHelpers.ValidateCaptureRequest(http.Request, normalized, db, isDev, globalHmacSecret, captureIpWhitelist, MaxCaptureRequestBytes, MaxImageBase64Chars, MaxMetadataJsonChars, cache);
            return v ?? await svc.ProcessAsync(normalized, "C++SDK抓拍");
        });
        captureGroup.MapPost("/onvif", async (HttpContext http, CaptureProcessingService svc) =>
        {
            var reqJson = await http.Request.ReadFromJsonAsync<JsonElement>();
            var normalized = new OnvifAdapter().Normalize(reqJson);
            var v = await AuraHelpers.ValidateCaptureRequest(http.Request, normalized, db, isDev, globalHmacSecret, captureIpWhitelist, MaxCaptureRequestBytes, MaxImageBase64Chars, MaxMetadataJsonChars, cache);
            return v ?? await svc.ProcessAsync(normalized, "ONVIF抓拍");
        });
        captureGroup.MapPost("/mock", async (CaptureMockReq req, CaptureOpsService svc) => await svc.CreateMockAsync(req)).RequireAuthorization("楼栋管理员");
        captureGroup.MapGet("/list", async (HttpRequest httpReq, CaptureOpsService svc) => await svc.GetCapturesAsync(httpReq)).RequireAuthorization("楼栋管理员");

        var retryGroup = app.MapGroup("/api/retry");
        retryGroup.MapGet("/status", async (RetryProcessingService svc) => await svc.GetStatusAsync()).RequireAuthorization("超级管理员");
        retryGroup.MapPost("/process", async (HttpRequest request, RetryProcessReq req, RetryProcessingService svc) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "retry.process", 30, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.ProcessAsync(req);
        }).RequireAuthorization("超级管理员");

        var roiGroup = app.MapGroup("/api/roi");
        roiGroup.MapGet("/list", async () =>
        {
            var rows = await db.GetRoisAsync();
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Rois.OrderByDescending(x => x.RoiId) });
        }).RequireAuthorization("楼栋管理员");
        roiGroup.MapPost("/save", async (RoiReq req) =>
        {
            var dbId = await db.InsertRoiAsync(req.CameraId, req.RoomNodeId, req.VerticesJson);
            if (dbId.HasValue)
            {
                await db.InsertOperationAsync("楼栋管理员", "ROI规则保存", $"CameraID={req.CameraId}, RoomID={req.RoomNodeId}");
                return Results.Ok(new { code = 0, msg = "保存成功", data = new { roiId = dbId.Value, req.CameraId, req.RoomNodeId, req.VerticesJson } });
            }
            var entity = new RoiEntity(Interlocked.Increment(ref store.RoiSeed), req.CameraId, req.RoomNodeId, req.VerticesJson, DateTimeOffset.Now);
            store.Rois.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "ROI规则保存", $"CameraID={req.CameraId}, RoomID={req.RoomNodeId}");
            return Results.Ok(new { code = 0, msg = "保存成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        var trackGroup = app.MapGroup("/api/track");
        trackGroup.MapGet("/{vid}", async (HttpRequest httpReq, string vid) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 500;
            var rows = await db.GetTrackEventsAsync(vid, limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.TrackEvents.Where(x => x.Vid == vid).OrderByDescending(x => x.EventTime).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        var judgeGroup = app.MapGroup("/api/judge");
        judgeGroup.MapPost("/run/home", async (HttpRequest request, JudgeRunReq req, JudgeService svc, EventDispatchService dispatch) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "judge.run.home", 1, TimeSpan.FromMinutes(10));
            if (rl is not null) return rl;
            var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
            var ret = await svc.RunHomeAsync(date);
            await dispatch.BroadcastRoleEventAsync("judge.updated", ret);
            return Results.Ok(new { code = 0, msg = "归寝研判完成", data = ret });
        }).RequireAuthorization("楼栋管理员");
        judgeGroup.MapPost("/run/daily", async (HttpRequest request, JudgeNightReq req, JudgeService svc, EventDispatchService dispatch) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "judge.run.daily", 1, TimeSpan.FromMinutes(10));
            if (rl is not null) return rl;
            var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
            var cutoff = req.CutoffHour < 0 || req.CutoffHour > 23 ? 23 : req.CutoffHour;
            var home = await svc.RunHomeAsync(date);
            var group = await svc.RunGroupRentAndStayAsync(date, 2, 120);
            var night = await svc.RunNightAbsenceAsync(date, cutoff);
            var summary = new[] { home, group, night };
            await dispatch.BroadcastRoleEventAsync("judge.updated", summary);
            return Results.Ok(new { code = 0, msg = "每日研判完成", data = summary });
        }).RequireAuthorization("楼栋管理员");
        judgeGroup.MapGet("/daily", async (HttpRequest httpReq, string? date) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 2000;
            var dateFilter = string.IsNullOrWhiteSpace(date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(date);
            var rows = await db.GetJudgeResultsAsync(dateFilter, null, limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.JudgeResults.Where(x => x.JudgeDate == dateFilter).OrderByDescending(x => x.JudgeId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        var alertGroup = app.MapGroup("/api/alert");
        alertGroup.MapGet("/list", async (HttpRequest httpReq) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 500;
            var rows = await db.GetAlertsAsync(limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Alerts.OrderByDescending(x => x.AlertId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");
        alertGroup.MapPost("/create", async (CreateAlertReq req) =>
        {
            var dbId = await db.InsertAlertAsync(req.AlertType, req.Detail);
            if (dbId.HasValue) return Results.Ok(new { code = 0, msg = "创建成功", data = new { alertId = dbId.Value, req.AlertType, req.Detail } });
            var entity = new AlertEntity(Interlocked.Increment(ref store.AlertSeed), req.AlertType, req.Detail, DateTimeOffset.Now);
            store.Alerts.Add(entity);
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        var statsGroup = app.MapGroup("/api/stats");
        statsGroup.MapGet("/overview", async (StatsApplicationService svc) => await svc.GetOverviewAsync()).RequireAuthorization("楼栋管理员");
        statsGroup.MapGet("/dashboard", async (StatsApplicationService svc) => await svc.GetDashboardAsync()).RequireAuthorization("楼栋管理员");

        var exportGroup = app.MapGroup("/api/export");
        exportGroup.MapGet("/{type}", async (HttpRequest request, string type, ExportApplicationService svc, string dataset = "capture", int maxRows = 5000) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "export", 5, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.ExportAsync(type, dataset, maxRows);
        }).RequireAuthorization("楼栋管理员");

        var outputGroup = app.MapGroup("/api/output");
        outputGroup.MapGet("/events", async (DateTimeOffset? from, DateTimeOffset? to, OutputApplicationService svc, int page = 1, int pageSize = 200) => await svc.GetEventsAsync(from, to, page, pageSize)).RequireAuthorization("超级管理员");
        outputGroup.MapGet("/persons", async (OutputApplicationService svc, int minCapture = 1) => await svc.GetPersonsAsync(minCapture)).RequireAuthorization("超级管理员");

        var vectorGroup = app.MapGroup("/api/vector");
        vectorGroup.MapPost("/extract", async (HttpRequest request, VectorExtractReq req, VectorApplicationService svc) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "vector.extract", 20, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.ExtractAsync(req);
        }).RequireAuthorization("楼栋管理员");
        vectorGroup.MapPost("/search", async (HttpRequest request, VectorSearchReq req, VectorApplicationService svc) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "vector.search", 60, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.SearchAsync(req);
        }).RequireAuthorization("楼栋管理员");

        var spaceGroup = app.MapGroup("/api/space");
        spaceGroup.MapPost("/collision/check", async (SpaceCollisionReq req, SpaceCollisionService svc) => await svc.CheckCollisionAsync(req)).RequireAuthorization("楼栋管理员");

        var clusterGroup = app.MapGroup("/api/cluster");
        clusterGroup.MapPost("/run", async (ClusterRunReq req, ClusterApplicationService svc) => await svc.RunAsync(req)).RequireAuthorization("超级管理员");
        clusterGroup.MapGet("/list", async (MonitoringQueryService svc) => await svc.GetClustersAsync()).RequireAuthorization("楼栋管理员");

        var operationGroup = app.MapGroup("/api/operation");
        operationGroup.MapGet("/list", async (OperationQueryService svc, string? keyword, int page = 1, int pageSize = 20) => await svc.GetOperationsAsync(keyword, page, pageSize)).RequireAuthorization("超级管理员");

        return app;
    }
}
