using Aura.Api.Hubs;
using Aura.Api.Internal;
using Aura.Api.Models;
using Aura.Api.Ops;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsCore
{
    public static void Map(IEndpointRouteBuilder app, AuraEndpointContext ctx)
    {
        var configuration = ctx.Configuration;
        var isDev = ctx.IsDev;
        var pgsql = ctx.PgSql;
        var audit = ctx.Audit;
        var cache = ctx.Cache;
        var store = ctx.Store;
        var allow = ctx.AllowInMemoryFallback;
        var alertNotifier = ctx.AlertNotifier;
        var ai = ctx.Ai;
        var readinessLogger = ctx.ReadinessLogger;

        app.MapHub<EventHub>("/hubs/events");

        app.MapGet("/", () => Results.Redirect("/index/"));
        app.MapGet("/api/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/api/health", () => Results.Ok(new { code = 0, msg = "寓瞳服务运行正常", time = DateTimeOffset.Now }));

        app.MapPost("/api/audit/page-view", async (HttpRequest request, HttpContext http, PageViewAuditReq req) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return AuraApiResults.Unauthorized();

            var rawPath = (req.PagePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 256 || !rawPath.StartsWith('/'))
            {
                return AuraApiResults.BadRequest("页面路径不合法", 40021);
            }

            var path = AuraHelpers.Sanitize(rawPath);
            var userName = http.User.Identity?.Name ?? "unknown";
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var eventTypeRaw = (req.EventType ?? "enter").Trim().ToLowerInvariant();
            var isLeave = eventTypeRaw == "leave";
            var eventName = isLeave ? "页面离开" : "页面进入";
            var dim = $"{userName}|{path}|{eventName}";
            var rl = await AuraHelpers.CheckRateLimitAsync(request, cache, "audit.page", 1, TimeSpan.FromMinutes(2), dim);
            if (rl is not null) return Results.Ok(new { code = 0, msg = "上报成功" });

            var title = AuraHelpers.Sanitize((req.PageTitle ?? string.Empty).Trim());
            var sessionId = AuraHelpers.Sanitize((req.SessionId ?? string.Empty).Trim());
            var stayMs = req.StayMs.GetValueOrDefault();
            if (stayMs < 0) stayMs = 0;
            if (stayMs > 7L * 24 * 60 * 60 * 1000) stayMs = 7L * 24 * 60 * 60 * 1000;
            var stayPart = isLeave ? $", 停留毫秒={stayMs}" : string.Empty;
            var titlePart = string.IsNullOrWhiteSpace(title) ? string.Empty : $", 标题={title}";
            var sessionPart = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : $", 会话={sessionId}";
            var detail = $"页面={path}{titlePart}{stayPart}{sessionPart}, IP={ip}";

            var opId = await audit.InsertOperationAsync(userName, eventName, detail);
            await audit.InsertSystemLogAsync("信息", "页面审计", $"用户={userName}, {detail}");
            if (!opId.HasValue && allow)
            {
                AuraHelpers.AddOperationLog(store, userName, eventName, detail);
                store.SystemLogs.Add(new SystemLogEntity(
                    SystemLogId: Interlocked.Increment(ref store.SystemLogSeed),
                    Level: "信息",
                    Source: "页面审计",
                    Message: $"用户={userName}, {detail}",
                    CreatedAt: DateTimeOffset.Now));
            }

            return Results.Ok(new { code = 0, msg = "上报成功" });
        }).RequireAuthorization();

        app.MapGet("/api/ops/readiness", async () =>
        {
            var now = DateTimeOffset.Now;
            var alertWindowMinutes = int.TryParse(configuration["Ops:Alert:HealthFailIfRecentFailureMinutes"], out var win) ? win : 10;

            var aiReady = false;
            var aiModelLoaded = false;
            try
            {
                var aiHealth = await ai.GetHealthAsync();
                aiReady = aiHealth != null;
                if (aiHealth.HasValue)
                {
                    aiModelLoaded = aiHealth.Value.TryGetProperty("model_loaded", out var p) && p.GetBoolean();
                }
            }
            catch (Exception ex)
            {
                readinessLogger.LogWarning(ex, "就绪检查发现 AI 服务连接异常");
            }

            var alertStats = alertNotifier.GetStats();
            var lastFailureAt = alertStats.LastFailureAt;
            var windowStart = now.AddMinutes(-alertWindowMinutes);
            var alertNotifyRecentFailure = alertWindowMinutes > 0
                && lastFailureAt.HasValue
                && lastFailureAt.GetValueOrDefault() >= windowStart;

            var jwtSecret = configuration["Jwt:Key"] ?? string.Empty;
            var hmacSecret = configuration["Security:HmacSecret"] ?? string.Empty;
            var pgsqlOk = await pgsql.TryPingAsync();

            var checks = new Dictionary<string, bool>
            {
                ["jwt"] = !string.IsNullOrWhiteSpace(jwtSecret) && !jwtSecret.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase),
                ["hmac"] = !string.IsNullOrWhiteSpace(hmacSecret) && !hmacSecret.Contains("PLEASE_", StringComparison.OrdinalIgnoreCase),
                ["pgsql"] = pgsqlOk,
                ["redis"] = cache.Enabled,
                ["ai_service"] = aiReady,
                ["ai_model"] = aiModelLoaded,
                ["alertNotify"] = !alertNotifyRecentFailure
            };
            var ready = checks.Values.All(v => v);
            return Results.Ok(new
            {
                code = 0,
                msg = ready ? "就绪检查通过" : "就绪检查未通过",
                data = new { environment = isDev ? "开发环境" : "生产环境", ready, checks },
                time = now
            });
        }).RequireAuthorization("超级管理员");

        app.MapPost("/api/ops/alert-notify-test", async (OpsAlertNotifyTestReq req) =>
        {
            var alertType = req.AlertType ?? "运维自检";
            await alertNotifier.NotifyAsync(new AlertNotifyMessage(alertType, req.Detail ?? "自检消息", "ops.test", DateTimeOffset.Now));
            await audit.InsertOperationAsync("系统管理员", "告警通知自检", $"类型={alertType}");
            return Results.Ok(new { code = 0, msg = "已发送" });
        }).RequireAuthorization("超级管理员");

        app.MapGet("/api/ops/alert-notify-stats", () => Results.Ok(new { code = 0, msg = "获取统计成功", data = alertNotifier.GetStats(), time = DateTimeOffset.Now }))
            .RequireAuthorization("超级管理员");
    }
}
