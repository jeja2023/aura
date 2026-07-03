using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Serialization;

namespace Aura.Api.Services;

internal sealed class ReportAutomationService
{
    private readonly ExtensionRepository _extensions;
    private readonly StatsApplicationService _stats;
    private readonly ILogger<ReportAutomationService> _logger;

    public ReportAutomationService(ExtensionRepository extensions, StatsApplicationService stats, ILogger<ReportAutomationService> logger)
    {
        _extensions = extensions;
        _stats = stats;
        _logger = logger;
    }

    public async Task<object?> GenerateAsync(long? scheduleId, string? reportType, DateOnly? rangeStart, DateOnly? rangeEnd, string? roleName, string? deliveryChannel, string? createdBy)
    {
        var type = NormalizeType(reportType);
        var end = rangeEnd ?? DateOnly.FromDateTime(DateTime.Now);
        var start = rangeStart ?? ComputeStart(type, end);
        if (start > end)
        {
            (start, end) = (end, start);
        }

        var dashboard = await _stats.GetDashboardAsync();
        var overview = await _stats.GetOverviewAsync();
        var summary = new
        {
            reportType = type,
            rangeStart = start,
            rangeEnd = end,
            generatedAt = DateTimeOffset.Now,
            overview,
            dashboard
        };
        var summaryJson = JsonSerializer.Serialize(summary, AuraJsonSerializerOptions.Default);
        var runId = await _extensions.CreateReportRunAsync(scheduleId, type, start, end, summaryJson, createdBy);
        if (!runId.HasValue)
        {
            return null;
        }

        var targetRole = string.IsNullOrWhiteSpace(roleName) ? "building_admin" : roleName.Trim();
        var channel = string.IsNullOrWhiteSpace(deliveryChannel) ? "system" : deliveryChannel.Trim();
        var deliveryId = await _extensions.CreateReportDeliveryAsync(runId.Value, targetRole, channel);
        _logger.LogInformation("报表已生成并投递。runId={RunId}, reportType={ReportType}, role={RoleName}, channel={Channel}", runId.Value, type, targetRole, channel);
        return new
        {
            runId = runId.Value,
            scheduleId,
            reportType = type,
            rangeStart = start,
            rangeEnd = end,
            roleName = targetRole,
            deliveryChannel = channel,
            deliveryId
        };
    }

    public async Task<int> RunDueSchedulesAsync()
    {
        var schedules = await _extensions.GetEnabledReportSchedulesAsync();
        var count = 0;
        foreach (var schedule in schedules)
        {
            var type = NormalizeType(schedule.ReportType);
            var end = DateOnly.FromDateTime(DateTime.Now);
            if (!IsDue(type, end))
            {
                continue;
            }
            var start = ComputeStart(type, end);
            var latest = await _extensions.GetLatestReportRunAsync(schedule.ScheduleId, start, end);
            if (latest.HasValue)
            {
                continue;
            }

            var generated = await GenerateAsync(schedule.ScheduleId, type, start, end, schedule.RoleName, schedule.DeliveryChannel, "report-automation");
            if (generated is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static string NormalizeType(string? value)
    {
        var text = (value ?? "daily").Trim().ToLowerInvariant();
        return text is "daily" or "weekly" or "monthly" ? text : "daily";
    }

    private static DateOnly ComputeStart(string reportType, DateOnly end)
    {
        return reportType switch
        {
            "weekly" => end.AddDays(-6),
            "monthly" => end.AddMonths(-1).AddDays(1),
            _ => end
        };
    }

    private static bool IsDue(string reportType, DateOnly date)
    {
        return reportType switch
        {
            "weekly" => date.DayOfWeek == DayOfWeek.Monday,
            "monthly" => date.Day == 1,
            _ => true
        };
    }
}

internal sealed class ReportAutomationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportAutomationHostedService> _logger;
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;

    public ReportAutomationHostedService(IServiceScopeFactory scopeFactory, ILogger<ReportAutomationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now - _lastRun >= TimeSpan.FromHours(1))
                {
                    _lastRun = DateTimeOffset.Now;
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ReportAutomationService>();
                    var generated = await service.RunDueSchedulesAsync();
                    if (generated > 0)
                    {
                        _logger.LogInformation("自动报表任务生成完成。count={Count}", generated);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动报表任务执行异常。");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}
