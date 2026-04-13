/* 文件：Hub、健康与运维端点 | File: Hub, health and ops endpoints */
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
        var db = ctx.Db;
        var cache = ctx.Cache;
        var alertNotifier = ctx.AlertNotifier;
        var ai = ctx.Ai;
        var readinessLogger = ctx.ReadinessLogger;

        app.MapHub<EventHub>("/hubs/events");

        app.MapGet("/", () => Results.Redirect("/index/"));
        // 负载均衡 / K8s 存活探针：无鉴权、无外部依赖，不暴露业务文案
        app.MapGet("/api/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/api/health", () => Results.Ok(new { code = 0, msg = "寓瞳中枢服务运行正常", time = DateTimeOffset.Now }));

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

            var jwtSecret = configuration["Jwt:Key"] ?? "";
            var hmacSecret = configuration["Security:HmacSecret"] ?? "";
            var pgsqlOk = await db.TryPingAsync();

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
    }
}
