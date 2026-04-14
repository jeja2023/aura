/* 文件：ROI、轨迹、研判、告警与统计等端点 | File: ROI, track, judge, alerts, stats, export, vector, cluster */
using Aura.Api.Data;
using Aura.Api.Clustering;
using Aura.Api.Ops;
using Aura.Api.Export;
using Aura.Api.Internal;
using Aura.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsDomain
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var db = ctx.Db;
        var cache = ctx.Cache;
        var store = ctx.Store;
        var allow = ctx.AllowInMemoryFallback;

        var roiGroup = app.MapGroup("/api/roi");
        roiGroup.MapGet("/list", async () =>
        {
            var rows = await db.GetRoisAsync();
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbRoi>() });
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
            if (!allow) return Results.Json(new { code = 50301, msg = "数据库写入失败，无法保存 ROI" }, statusCode: 503);
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
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbTrackEvent>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.TrackEvents.Where(x => x.Vid == vid).OrderByDescending(x => x.EventTime).Take(limit) });
        }).RequireAuthorization("楼栋管理员");
        trackGroup.MapGet("/history/list", async (HttpRequest httpReq) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 200;
            var rows = await db.GetTrackEventsAsync(null, limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbTrackEvent>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.TrackEvents.OrderByDescending(x => x.EventTime).Take(limit) });
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
        judgeGroup.MapPost("/run/abnormal", async (HttpRequest request, JudgeAbnormalReq req, JudgeService svc, EventDispatchService dispatch) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "judge.run.abnormal", 1, TimeSpan.FromMinutes(10));
            if (rl is not null) return rl;
            var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
            var groupThreshold = req.GroupThreshold <= 0 ? 2 : req.GroupThreshold;
            var stayMinutes = req.StayMinutes <= 0 ? 120 : req.StayMinutes;
            var ret = await svc.RunGroupRentAndStayAsync(date, groupThreshold, stayMinutes);
            await dispatch.BroadcastRoleEventAsync("judge.updated", ret);
            return Results.Ok(new { code = 0, msg = "群租/滞留研判完成", data = ret });
        }).RequireAuthorization("楼栋管理员");
        judgeGroup.MapPost("/run/night", async (HttpRequest request, JudgeNightReq req, JudgeService svc, EventDispatchService dispatch) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "judge.run.night", 1, TimeSpan.FromMinutes(10));
            if (rl is not null) return rl;
            var date = string.IsNullOrWhiteSpace(req.Date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(req.Date);
            var cutoff = req.CutoffHour < 0 || req.CutoffHour > 23 ? 23 : req.CutoffHour;
            var ret = await svc.RunNightAbsenceAsync(date, cutoff);
            await dispatch.BroadcastRoleEventAsync("judge.updated", ret);
            return Results.Ok(new { code = 0, msg = "夜不归宿研判完成", data = ret });
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
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbJudgeResult>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.JudgeResults.Where(x => x.JudgeDate == dateFilter).OrderByDescending(x => x.JudgeId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        var alertGroup = app.MapGroup("/api/alert");
        alertGroup.MapGet("/list", async (HttpRequest httpReq) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 500;
            var rows = await db.GetAlertsAsync(limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbAlert>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Alerts.OrderByDescending(x => x.AlertId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");
        alertGroup.MapPost("/create", async (CreateAlertReq req) =>
        {
            var dbId = await db.InsertAlertAsync(req.AlertType, req.Detail);
            if (dbId.HasValue) return Results.Ok(new { code = 0, msg = "创建成功", data = new { alertId = dbId.Value, req.AlertType, req.Detail } });
            if (!allow) return Results.Json(new { code = 50301, msg = "数据库写入失败，无法创建告警" }, statusCode: 503);
            var entity = new AlertEntity(Interlocked.Increment(ref store.AlertSeed), req.AlertType, req.Detail, DateTimeOffset.Now);
            store.Alerts.Add(entity);
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        var statsGroup = app.MapGroup("/api/stats");
        statsGroup.MapGet("/overview", async (StatsApplicationService svc) =>
        {
            try
            {
                var data = await svc.GetOverviewAsync();
                return Results.Ok(new { code = 0, msg = "查询成功", data });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { code = 50001, msg = $"概览查询失败：{ex.Message}" });
            }
        }).RequireAuthorization("楼栋管理员");
        statsGroup.MapGet("/dashboard", async (StatsApplicationService svc) =>
        {
            try
            {
                var data = await svc.GetDashboardAsync();
                return Results.Ok(new { code = 0, msg = "查询成功", data });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { code = 50002, msg = $"图表数据查询失败：{ex.Message}" });
            }
        }).RequireAuthorization("楼栋管理员");

        var exportGroup = app.MapGroup("/api/export");
        exportGroup.MapGet("/{type}", async (HttpRequest request, string type, ExportApplicationService svc, string dataset = "capture", int maxRows = 5000, string? keyword = null) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "export", 5, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.ExportAsync(type, dataset, maxRows, keyword);
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

        var systemLogGroup = app.MapGroup("/api/system-log");
        systemLogGroup.MapGet("/list", async (SystemLogQueryService svc, string? keyword, int page = 1, int pageSize = 20) => await svc.GetSystemLogsAsync(keyword, page, pageSize)).RequireAuthorization("超级管理员");
    }
}
