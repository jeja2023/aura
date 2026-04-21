using Aura.Api.Data;

internal sealed class StatsApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly CaptureRepository _captureRepository;
    private readonly MonitoringRepository _monitoringRepository;
    private readonly DeviceRepository _deviceRepository;

    public StatsApplicationService(
        AppStore store,
        PgSqlConnectionFactory pgSqlConnectionFactory,
        CaptureRepository captureRepository,
        MonitoringRepository monitoringRepository,
        DeviceRepository deviceRepository)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _captureRepository = captureRepository;
        _monitoringRepository = monitoringRepository;
        _deviceRepository = deviceRepository;
    }

    public async Task<object> GetOverviewAsync()
    {
        var totalCaptureDb = await _captureRepository.GetCaptureCountAsync();
        var totalAlertDb = await _monitoringRepository.GetAlertCountAsync();
        var devices = await _deviceRepository.GetDevicesAsync();
        var useDb = _pgSqlConnectionFactory.IsConfigured;

        var totalCapture = useDb
            ? Math.Max(0, (int)(totalCaptureDb ?? 0))
            : totalCaptureDb.HasValue && totalCaptureDb.Value >= 0
            ? totalCaptureDb.Value
            : _store.Captures.Count;
        var totalAlert = useDb
            ? Math.Max(0, (int)(totalAlertDb ?? 0))
            : totalAlertDb.HasValue && totalAlertDb.Value >= 0
            ? totalAlertDb.Value
            : _store.Alerts.Count;
        var onlineDevice = useDb
            ? devices.Count(x => x.Status == "online")
            : devices.Count > 0
            ? devices.Count(x => x.Status == "online")
            : _store.Devices.Count(x => x.Status == "online");
        return new { totalCapture, totalAlert, onlineDevice };
    }

    public async Task<object> GetDashboardAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);
        var end = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var rangeStart = new DateTimeOffset(start);
        var rangeEnd = new DateTimeOffset(end);

        var captures = await _captureRepository.GetCapturesInRangeAsync(rangeStart, rangeEnd);
        var alerts = await _monitoringRepository.GetAlertsInRangeAsync(rangeStart, rangeEnd);
        var useDb = _pgSqlConnectionFactory.IsConfigured;

        var sourceCaptures = useDb
            ? captures.Select(x => new { x.DeviceId, CaptureTime = new DateTimeOffset(DateTime.SpecifyKind(x.CaptureTime, DateTimeKind.Utc)).ToLocalTime() }).ToList()
            : captures.Count > 0
            ? captures.Select(x => new { x.DeviceId, CaptureTime = new DateTimeOffset(DateTime.SpecifyKind(x.CaptureTime, DateTimeKind.Utc)).ToLocalTime() }).ToList()
            : _store.Captures
                .Where(x => x.CaptureTime >= rangeStart && x.CaptureTime < rangeEnd)
                .Select(x => new { x.DeviceId, x.CaptureTime })
                .ToList();
        var sourceAlerts = useDb
            ? alerts.Select(x => new { x.AlertType, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
            : alerts.Count > 0
            ? alerts.Select(x => new { x.AlertType, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
            : _store.Alerts
                .Where(x => x.CreatedAt >= rangeStart && x.CreatedAt < rangeEnd)
                .Select(x => new { x.AlertType, x.CreatedAt })
                .ToList();

        var daily = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(-6 + i))
            .Select(d => new
            {
                day = d.ToString("MM-dd"),
                captureCount = sourceCaptures.Count(x => DateOnly.FromDateTime(x.CaptureTime.DateTime) == d),
                alertCount = sourceAlerts.Count(x => DateOnly.FromDateTime(x.CreatedAt.DateTime) == d)
            }).ToList();
        var byDevice = sourceCaptures
            .GroupBy(x => x.DeviceId)
            .Select(g => new { deviceId = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(10)
            .ToList();
        var byAlertType = sourceAlerts
            .GroupBy(x => string.IsNullOrWhiteSpace(x.AlertType) ? "unknown" : x.AlertType)
            .Select(g => new { alertType = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();
        return new { daily, byDevice, byAlertType };
    }
}
