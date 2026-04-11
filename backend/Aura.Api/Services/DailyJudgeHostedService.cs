namespace Aura.Api.Services;

public sealed class DailyJudgeScheduleState 
{ 
    public Func<DateOnly, Task>? RunDailyAsync { get; set; } 
}

internal sealed class DailyJudgeHostedService : BackgroundService
{
    private readonly DailyJudgeScheduleState _state;
    private readonly ILogger<DailyJudgeHostedService> _logger;
    private DateOnly? _lastDate;

    public DailyJudgeHostedService(DailyJudgeScheduleState state, ILogger<DailyJudgeHostedService> logger) 
    { 
        _state = state; 
        _logger = logger; 
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var today = DateOnly.FromDateTime(now);
                if (now.Hour == 0 && now.Minute < 5 && _lastDate != today)
                {
                    var run = _state.RunDailyAsync;
                    if (run is not null)
                    {
                        var startedAt = DateTimeOffset.Now;
                        await run(today);
                        _lastDate = today;
                        var costMs = (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
                        _logger.LogInformation("每日研判后台任务执行完成。date={Date}, costMs={CostMs}", today, costMs);
                    }
                }
            }
            catch (Exception ex) 
            { 
                _logger.LogError(ex, "每日研判后台任务执行异常。"); 
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
