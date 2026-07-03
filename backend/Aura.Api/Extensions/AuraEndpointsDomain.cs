using Aura.Api.Clustering;
using Aura.Api.Data;
using Aura.Api.Export;
using Aura.Api.Internal;
using Aura.Api.Models;
using Aura.Api.Ops;
using Aura.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsDomain
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var capture = ctx.Capture;
        var audit = ctx.Audit;
        var monitoring = ctx.Monitoring;
        var cache = ctx.Cache;
        var store = ctx.Store;
        var allow = ctx.AllowInMemoryFallback;
        var pgSqlConnectionFactory = app.ServiceProvider.GetRequiredService<PgSqlConnectionFactory>();

        var roiGroup = app.MapGroup("/api/roi");
        roiGroup.MapGet("/list", async (HttpRequest httpReq) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l)
                ? Math.Clamp(l, 1, CaptureRepository.MaxRoiLimit)
                : CaptureRepository.DefaultRoiLimit;
            var rows = await capture.GetRoisAsync(limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbRoi>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Rois.OrderByDescending(x => x.RoiId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        roiGroup.MapPost("/save", async (RoiReq req) =>
        {
            var dbId = await capture.InsertRoiAsync(req.CameraId, req.RoomNodeId, req.VerticesJson);
            if (dbId.HasValue)
            {
                await audit.InsertOperationAsync("楼栋管理员", "ROI规则保存", $"CameraID={req.CameraId}, RoomID={req.RoomNodeId}");
                return Results.Ok(new { code = 0, msg = "保存成功", data = new { roiId = dbId.Value, req.CameraId, req.RoomNodeId, req.VerticesJson } });
            }

            if (!allow) return AuraApiResults.ServiceUnavailable("数据库写入失败，无法保存 ROI", 50301);
            var entity = new RoiEntity(Interlocked.Increment(ref store.RoiSeed), req.CameraId, req.RoomNodeId, req.VerticesJson, DateTimeOffset.Now);
            store.Rois.Add(entity);
            AuraHelpers.AddOperationLog(store, "楼栋管理员", "ROI规则保存", $"CameraID={req.CameraId}, RoomID={req.RoomNodeId}");
            return Results.Ok(new { code = 0, msg = "保存成功", data = entity });
        }).RequireAuthorization("楼栋管理员");

        var trackGroup = app.MapGroup("/api/track");
        trackGroup.MapGet("/{vid}", async (HttpRequest httpReq, string vid) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 500;
            limit = Math.Clamp(limit, 1, 2000);
            var rows = await capture.GetTrackEventsAsync(vid, limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbTrackEvent>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.TrackEvents.Where(x => x.Vid == vid).OrderByDescending(x => x.EventTime).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        trackGroup.MapGet("/history/list", async (HttpRequest httpReq) =>
        {
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : 200;
            limit = Math.Clamp(limit, 1, 2000);
            var rows = await capture.GetTrackEventsAsync(null, limit);
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
            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : MonitoringRepository.DefaultJudgeLimit;
            limit = Math.Clamp(limit, 1, MonitoringRepository.MaxJudgeLimit);
            var dateFilter = string.IsNullOrWhiteSpace(date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(date);
            var rows = await monitoring.GetJudgeResultsAsync(dateFilter, null, limit);
            if (rows.Count > 0) return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbJudgeResult>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.JudgeResults.Where(x => x.JudgeDate == dateFilter).OrderByDescending(x => x.JudgeId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        var alertGroup = app.MapGroup("/api/alert");
        alertGroup.MapGet("/list", async (HttpRequest httpReq) =>
        {
            if (int.TryParse(httpReq.Query["page"].FirstOrDefault(), out var pageNum) && pageNum > 0)
            {
                var pageSize = int.TryParse(httpReq.Query["pageSize"].FirstOrDefault(), out var ps)
                    ? Math.Clamp(ps, 1, MonitoringRepository.MaxAlertPageSize)
                    : MonitoringRepository.DefaultAlertPageSize;
                var typeKeyword = httpReq.Query["typeKeyword"].FirstOrDefault();
                var detailKeyword = httpReq.Query["detailKeyword"].FirstOrDefault();

                DateTimeOffset? from = null;
                DateTimeOffset? to = null;
                var fromQ = httpReq.Query["from"].FirstOrDefault();
                var toQ = httpReq.Query["to"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(fromQ) && DateTimeOffset.TryParse(fromQ, out var parsedFrom))
                {
                    from = parsedFrom;
                }

                if (!string.IsNullOrWhiteSpace(toQ) && DateTimeOffset.TryParse(toQ, out var parsedTo))
                {
                    to = parsedTo;
                }

                var pagedResult = await monitoring.GetAlertsPagedAsync(typeKeyword, detailKeyword, from, to, pageNum, pageSize);
                if (pgSqlConnectionFactory.IsConfigured)
                {
                    if (!pagedResult.Succeeded)
                    {
                        return AuraApiResults.ServiceUnavailable("数据库查询失败，无法获取告警列表", 50311);
                    }

                    return Results.Ok(new { code = 0, msg = "查询成功", data = pagedResult.Rows, pager = new { page = pageNum, pageSize, total = pagedResult.Total } });
                }

                if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbAlert>(), pager = new { page = pageNum, pageSize, total = 0 } });

                IEnumerable<AlertEntity> mem = store.Alerts;
                if (!string.IsNullOrWhiteSpace(typeKeyword))
                {
                    var keyword = typeKeyword.Trim();
                    mem = mem.Where(x => x.AlertType.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(detailKeyword))
                {
                    var keyword = detailKeyword.Trim();
                    mem = mem.Where(x => x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                }

                if (from.HasValue)
                {
                    mem = mem.Where(x => x.CreatedAt >= from.Value);
                }

                if (to.HasValue)
                {
                    mem = mem.Where(x => x.CreatedAt <= to.Value);
                }

                var ordered = mem.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.AlertId).ToList();
                var memTotal = ordered.Count;
                var slice = ordered.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
                return Results.Ok(new { code = 0, msg = "查询成功", data = slice, pager = new { page = pageNum, pageSize, total = memTotal } });
            }

            var limit = int.TryParse(httpReq.Query["limit"].FirstOrDefault(), out var l) ? l : MonitoringRepository.DefaultAlertLimit;
            limit = Math.Clamp(limit, 1, MonitoringRepository.MaxAlertLimit);
            var limitedResult = await monitoring.GetAlertsPagedAsync(null, null, null, null, 1, limit);
            if (pgSqlConnectionFactory.IsConfigured)
            {
                if (!limitedResult.Succeeded)
                {
                    return AuraApiResults.ServiceUnavailable("数据库查询失败，无法获取告警列表", 50311);
                }

                return Results.Ok(new { code = 0, msg = "查询成功", data = limitedResult.Rows });
            }
            if (!allow) return Results.Ok(new { code = 0, msg = "查询成功", data = new List<DbAlert>() });
            return Results.Ok(new { code = 0, msg = "查询成功", data = store.Alerts.OrderByDescending(x => x.AlertId).Take(limit) });
        }).RequireAuthorization("楼栋管理员");

        alertGroup.MapPost("/create", async (CreateAlertReq req) =>
        {
            var dbId = await monitoring.InsertAlertAsync(req.AlertType, req.Detail);
            if (dbId.HasValue) return Results.Ok(new { code = 0, msg = "创建成功", data = new { alertId = dbId.Value, req.AlertType, req.Detail } });
            if (!allow) return AuraApiResults.ServiceUnavailable("数据库写入失败，无法创建告警", 50301);
            var entity = new AlertEntity(Interlocked.Increment(ref store.AlertSeed), req.AlertType, req.Detail, DateTimeOffset.Now);
            store.Alerts.Add(entity);
            return Results.Ok(new { code = 0, msg = "创建成功", data = entity });
        }).RequireAuthorization("告警操作");

        alertGroup.MapGet("/workflow/list", async (ExtensionRepository extensions, string? status, int limit = 100) =>
        {
            var rows = await extensions.GetAlertWorkflowsAsync(status, limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("告警操作");

        alertGroup.MapPost("/{alertId:long}/workflow", async (HttpContext http, long alertId, AlertWorkflowUpdateReq req, ExtensionRepository extensions) =>
        {
            var userName = http.User.Identity?.Name ?? "system";
            var workflowId = await extensions.UpsertAlertWorkflowAsync(alertId, req, userName);
            if (!workflowId.HasValue) return AuraApiResults.ServiceUnavailable("告警流程写入失败", 50301);
            await audit.InsertOperationAsync(userName, "告警闭环更新", $"alertId={alertId}, workflowId={workflowId.Value}, status={req.Status}");
            return Results.Ok(new { code = 0, msg = "告警流程已更新", data = new { workflowId = workflowId.Value, alertId } });
        }).RequireAuthorization("告警操作");

        var statsGroup = app.MapGroup("/api/stats");
        statsGroup.MapGet("/overview", async (StatsApplicationService svc) =>
        {
            try
            {
                var data = await svc.GetOverviewAsync();
                return Results.Ok(new { code = 0, msg = "查询成功", data });
            }
            catch
            {
                return AuraApiResults.InternalServerError("概览查询失败", 50001);
            }
        }).RequireAuthorization("楼栋管理员");

        statsGroup.MapGet("/dashboard", async (StatsApplicationService svc) =>
        {
            try
            {
                var data = await svc.GetDashboardAsync();
                return Results.Ok(new { code = 0, msg = "查询成功", data });
            }
            catch
            {
                return AuraApiResults.InternalServerError("图表数据查询失败", 50002);
            }
        }).RequireAuthorization("楼栋管理员");

        var exportGroup = app.MapGroup("/api/export");
        exportGroup.MapGet("/{type}", async (HttpRequest request, string type, ExportApplicationService svc, string dataset = "capture", int maxRows = 5000, string? keyword = null, DateTimeOffset? from = null, DateTimeOffset? to = null, long? deviceId = null, int? channelNo = null, string? typeKeyword = null, string? detailKeyword = null) =>
        {
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "export", 5, TimeSpan.FromMinutes(1));
            if (rl is not null) return rl;
            return await svc.ExportAsync(type, dataset, maxRows, keyword, new ExportApplicationService.ExportFilterOptions(from, to, deviceId, channelNo, typeKeyword, detailKeyword));
        }).RequireAuthorization("数据导出");

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

        spaceGroup.MapGet("/topology", async (ExtensionRepository extensions, int limit = 1000) =>
        {
            var rows = await extensions.GetSpaceTopologyAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("SpaceManage");

        spaceGroup.MapPost("/topology", async (SpaceTopologyEdgeReq req, ExtensionRepository extensions) =>
        {
            var edgeId = await extensions.CreateSpaceTopologyEdgeAsync(req);
            if (!edgeId.HasValue) return AuraApiResults.ServiceUnavailable("空间拓扑写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "空间拓扑已保存", data = new { edgeId = edgeId.Value } });
        }).RequireAuthorization("SpaceManage");

        spaceGroup.MapGet("/heatmap", async (ExtensionRepository extensions, long? floorId, int limit = 100) =>
        {
            var rows = await extensions.GetSpaceHeatmapsAsync(floorId, limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("SpaceManage");

        spaceGroup.MapPost("/heatmap", async (SpaceHeatmapSnapshotReq req, ExtensionRepository extensions) =>
        {
            var snapshotId = await extensions.CreateSpaceHeatmapAsync(req);
            if (!snapshotId.HasValue) return AuraApiResults.ServiceUnavailable("热力图快照写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "热力图快照已保存", data = new { snapshotId = snapshotId.Value } });
        }).RequireAuthorization("SpaceManage");

        var clusterGroup = app.MapGroup("/api/cluster");
        clusterGroup.MapPost("/run", async (ClusterRunReq req, ClusterApplicationService svc) => await svc.RunAsync(req)).RequireAuthorization("超级管理员");
        clusterGroup.MapGet("/list", async (MonitoringQueryService svc) => await svc.GetClustersAsync()).RequireAuthorization("楼栋管理员");

        var operationGroup = app.MapGroup("/api/operation");
                operationGroup.MapGet("/list", async (OperationQueryService svc, string? keyword, DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 20) =>
        {
            var result = await svc.GetOperationsAsync(keyword, page, pageSize, from, to);
            if (!result.Succeeded) return AuraApiResults.ServiceUnavailable("数据库查询失败，无法获取操作日志", 50311);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Data, pager = result.Pager });
        }).RequireAuthorization("超级管理员");

        var systemLogGroup = app.MapGroup("/api/system-log");
                systemLogGroup.MapGet("/list", async (SystemLogQueryService svc, string? keyword, DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 20) =>
        {
            var result = await svc.GetSystemLogsAsync(keyword, page, pageSize, from, to);
            if (!result.Succeeded) return AuraApiResults.ServiceUnavailable("数据库查询失败，无法获取系统日志", 50311);
            return Results.Ok(new { code = 0, msg = "查询成功", data = result.Data, pager = result.Pager });
        }).RequireAuthorization("超级管理员");

        var reportGroup = app.MapGroup("/api/report");
        reportGroup.MapGet("/schedule/list", async (ExtensionRepository extensions, int limit = 200) =>
        {
            var rows = await extensions.GetReportSchedulesAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("ReportManage");

        reportGroup.MapPost("/schedule", async (HttpContext http, ReportScheduleReq req, ExtensionRepository extensions) =>
        {
            var scheduleId = await extensions.CreateReportScheduleAsync(req, http.User.Identity?.Name ?? "system");
            if (!scheduleId.HasValue) return AuraApiResults.ServiceUnavailable("报表计划写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "报表计划已保存", data = new { scheduleId = scheduleId.Value } });
        }).RequireAuthorization("ReportManage");

        reportGroup.MapGet("/run/list", async (ExtensionRepository extensions, int limit = 100) =>
        {
            var rows = await extensions.GetReportRunsAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("ReportManage");

        reportGroup.MapPost("/generate", async (HttpContext http, ReportGenerateReq req, ReportAutomationService svc) =>
        {
            var result = await svc.GenerateAsync(req.ScheduleId, req.ReportType, req.RangeStart, req.RangeEnd, req.RoleName, req.DeliveryChannel, http.User.Identity?.Name ?? "system");
            if (result is null) return AuraApiResults.ServiceUnavailable("报表生成失败", 50301);
            return Results.Ok(new { code = 0, msg = "报表已生成并投递", data = result });
        }).RequireAuthorization("ReportManage");

        var tenantGroup = app.MapGroup("/api/tenant");
        tenantGroup.MapGet("/list", async (ExtensionRepository extensions, int limit = 200) =>
        {
            var rows = await extensions.GetTenantsAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("TenantManage");

        tenantGroup.MapPost("/project", async (TenantProjectReq req, ExtensionRepository extensions) =>
        {
            var tenantId = await extensions.CreateTenantAsync(req);
            if (!tenantId.HasValue) return AuraApiResults.ServiceUnavailable("租户项目写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "租户项目已保存", data = new { tenantId = tenantId.Value } });
        }).RequireAuthorization("TenantManage");

        tenantGroup.MapGet("/scope/list", async (ExtensionRepository extensions, int limit = 200) =>
        {
            var rows = await extensions.GetTenantRoleScopesAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("TenantManage");

        tenantGroup.MapPost("/scope", async (TenantRoleScopeReq req, ExtensionRepository extensions) =>
        {
            var scopeId = await extensions.UpsertTenantRoleScopeAsync(req);
            if (!scopeId.HasValue) return AuraApiResults.ServiceUnavailable("租户权限范围写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "租户权限范围已保存", data = new { scopeId = scopeId.Value } });
        }).RequireAuthorization("TenantManage");

        var aiPlatformGroup = app.MapGroup("/api/ai-platform");
        aiPlatformGroup.MapGet("/providers", async (ExtensionRepository extensions, int limit = 200) =>
        {
            var rows = await extensions.GetAiProvidersAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("AiPlatform");

        aiPlatformGroup.MapPost("/providers", async (AiProviderConfigReq req, ExtensionRepository extensions) =>
        {
            var providerId = await extensions.CreateAiProviderAsync(req);
            if (!providerId.HasValue) return AuraApiResults.ServiceUnavailable("AI 供应商写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "AI 供应商已保存", data = new { providerId = providerId.Value } });
        }).RequireAuthorization("AiPlatform");

        aiPlatformGroup.MapGet("/experiments", async (ExtensionRepository extensions, int limit = 200) =>
        {
            var rows = await extensions.GetAiExperimentsAsync(limit);
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }).RequireAuthorization("AiPlatform");

        aiPlatformGroup.MapPost("/experiments", async (AiAbExperimentReq req, ExtensionRepository extensions) =>
        {
            var experimentId = await extensions.CreateAiExperimentAsync(req);
            if (!experimentId.HasValue) return AuraApiResults.ServiceUnavailable("AI 实验写入失败", 50301);
            return Results.Ok(new { code = 0, msg = "AI 实验已保存", data = new { experimentId = experimentId.Value } });
        }).RequireAuthorization("AiPlatform");
    }
}
