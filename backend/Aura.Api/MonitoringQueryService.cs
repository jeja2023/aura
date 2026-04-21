using Aura.Api.Data;
using Aura.Api.Models;
using Aura.Api.Ops;

internal sealed class MonitoringQueryService
{
    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly CaptureRepository _captureRepository;
    private readonly MonitoringRepository _monitoringRepository;
    private readonly AuditRepository _auditRepository;
    private readonly EventDispatchService _eventDispatchService;

    public MonitoringQueryService(
        AppStore store,
        PgSqlConnectionFactory pgSqlConnectionFactory,
        CaptureRepository captureRepository,
        MonitoringRepository monitoringRepository,
        AuditRepository auditRepository,
        EventDispatchService eventDispatchService)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _captureRepository = captureRepository;
        _monitoringRepository = monitoringRepository;
        _auditRepository = auditRepository;
        _eventDispatchService = eventDispatchService;
    }

    public async Task<IResult> GetTrackAsync(string vid, int limit)
    {
        const int defaultLimit = 500;
        const int maxLimit = 2000;
        var lim = limit <= 0 ? defaultLimit : Math.Clamp(limit, 1, maxLimit);

        var rows = await _captureRepository.GetTrackEventsAsync(vid, lim);
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = new { vid, limit = lim, points = rows.Select(x => new { x.CameraId, x.RoiId, time = x.EventTime }) } });
        }

        var points = _store.TrackEvents
            .Where(x => x.Vid == vid)
            .OrderByDescending(x => x.EventTime)
            .Take(lim)
            .Select(x => new { x.CameraId, x.RoiId, time = x.EventTime });
        return Results.Ok(new { code = 0, msg = "查询成功", data = new { vid, limit = lim, points } });
    }

    public async Task<IResult> GetJudgeDailyAsync(string? date, int limit)
    {
        const int defaultLimit = 2000;
        const int maxLimit = 5000;
        var lim = limit <= 0 ? defaultLimit : Math.Clamp(limit, 1, maxLimit);
        var day = string.IsNullOrWhiteSpace(date) ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.Parse(date);
        var rows = await _monitoringRepository.GetJudgeResultsAsync(day, null, lim);
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows, limit = lim });
        }

        return Results.Ok(new
        {
            code = 0,
            msg = "查询成功",
            data = _store.JudgeResults.Where(x => x.JudgeDate == day).OrderByDescending(x => x.JudgeId).Take(lim),
            limit = lim
        });
    }

    public async Task<IResult> GetAlertsAsync(int limit)
    {
        const int defaultLimit = 500;
        const int maxLimit = 2000;
        var lim = limit <= 0 ? defaultLimit : Math.Clamp(limit, 1, maxLimit);

        var rows = await _monitoringRepository.GetAlertsAsync(lim);
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            var mapped = rows.Select(x => new AlertEntity(x.AlertId, x.AlertType, x.Detail, x.CreatedAt));
            return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
        }

        return Results.Ok(new { code = 0, msg = "查询成功", data = _store.Alerts.OrderByDescending(x => x.AlertId).Take(lim) });
    }

    public async Task<IResult> CreateAlertAsync(CreateAlertReq req)
    {
        var entity = new AlertEntity(Interlocked.Increment(ref _store.AlertSeed), req.AlertType, req.Detail, DateTimeOffset.Now);
        var dbId = await _monitoringRepository.InsertAlertAsync(entity.AlertType, entity.Detail);
        var saved = dbId.HasValue ? entity with { AlertId = dbId.Value } : entity;
        if (!dbId.HasValue)
        {
            _store.Alerts.Add(saved);
        }

        await _auditRepository.InsertOperationAsync("楼栋管理员", "手动告警", $"类型={req.AlertType}");
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: "楼栋管理员",
            Action: "手动告警",
            Detail: $"类型={req.AlertType}",
            CreatedAt: DateTimeOffset.Now));
        await _eventDispatchService.NotifyAlertAsync(saved.AlertType, saved.Detail, "手动告警");
        await _eventDispatchService.BroadcastRoleEventAsync("alert.created", new { alertType = saved.AlertType, detail = saved.Detail, at = saved.CreatedAt });
        return Results.Ok(new { code = 0, msg = "告警创建成功", data = saved });
    }

    public async Task<IResult> GetClustersAsync()
    {
        var rows = await _monitoringRepository.GetVirtualPersonsAsync();
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }

        return Results.Ok(new { code = 0, msg = "查询成功", data = _store.VirtualPersons.OrderByDescending(x => x.FirstSeen) });
    }
}
