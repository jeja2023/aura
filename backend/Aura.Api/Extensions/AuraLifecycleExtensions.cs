using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Services;

namespace Aura.Api.Extensions;

public static class AuraLifecycleExtensions
{
    public static WebApplication ConfigureAuraLifecycle(this WebApplication app, bool exposePrometheus, bool tracingRequested, bool tracingConfigured)
    {
        app.ConfigureDailyJudgeSchedule();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Aura API started successfully.");
            logger.LogInformation("Listening on: {Urls}", string.Join(", ", app.Urls));
            logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);
            logger.LogInformation(
                "Observability: Prometheus={Prometheus}; Tracing={Tracing}",
                exposePrometheus ? "enabled" : "disabled",
                tracingConfigured ? "enabled" : "disabled");
            if (tracingRequested && !tracingConfigured)
            {
                logger.LogWarning("Ops:Telemetry:EnableTracing is enabled, but no OTLP endpoint is configured. Tracing stays disabled.");
            }

            logger.LogInformation("Press Ctrl+C to stop the service.");
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Shutdown signal received. Stopping service...");
        });
        return app;
    }

    private static void ConfigureDailyJudgeSchedule(this WebApplication app)
    {
        var dailyJudgeState = app.Services.GetRequiredService<DailyJudgeScheduleState>();
        var cache = app.Services.GetRequiredService<RedisCacheService>();

        dailyJudgeState.RunDailyAsync = async today =>
        {
            using var scope = app.Services.CreateScope();
            var judgeService = scope.ServiceProvider.GetRequiredService<JudgeService>();
            var auditRepository = scope.ServiceProvider.GetRequiredService<AuditRepository>();

            const string lockKey = "aura:lock:daily-judges";
            string? lockToken = await cache.TryAcquireLockAsync(lockKey, TimeSpan.FromMinutes(60));
            if (lockToken is null && cache.Enabled)
            {
                return;
            }

            try
            {
                await judgeService.RunHomeAsync(today);
                await judgeService.RunGroupRentAndStayAsync(today, 2, 120);
                await judgeService.RunNightAbsenceAsync(today, 23);
                await auditRepository.InsertOperationAsync("系统任务", "归寝定时任务", $"日期={today:yyyy-MM-dd}");
            }
            finally
            {
                if (lockToken is not null)
                {
                    await cache.ReleaseLockAsync(lockKey, lockToken);
                }
            }
        };
    }
}
