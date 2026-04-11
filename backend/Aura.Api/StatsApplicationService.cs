using Aura.Api.Data;

internal sealed class StatsApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;

    public StatsApplicationService(AppStore store, PgSqlStore db)
    {
        _store = store;
        _db = db;
    }

    public async Task<object> GetOverviewAsync()
    {
        var captures = await _db.GetCapturesAsync();
        var alerts = await _db.GetAlertsAsync();
        var devices = await _db.GetDevicesAsync();
        var sourceCaptures = captures.Count > 0
            ? captures.Select(x => new { x.DeviceId, CaptureTime = new DateTimeOffset(x.CaptureTime) }).ToList()
            : _store.Captures.Select(x => new { x.DeviceId, x.CaptureTime }).ToList();
        var totalCapture = sourceCaptures.Count;
        var totalAlert = alerts.Count > 0 ? alerts.Count : _store.Alerts.Count;
        var onlineDevice = devices.Count > 0 ? devices.Count(x => x.Status == "online") : _store.Devices.Count(x => x.Status == "online");
        return new { totalCapture, totalAlert, onlineDevice };
    }

    public async Task<object> GetDashboardAsync()
    {
        var captures = await _db.GetCapturesAsync();
        var alerts = await _db.GetAlertsAsync();
        var sourceCaptures = captures.Count > 0
            ? captures.Select(x => new { x.DeviceId, CaptureTime = new DateTimeOffset(x.CaptureTime) }).ToList()
            : _store.Captures.Select(x => new { x.DeviceId, x.CaptureTime }).ToList();
        var sourceAlerts = alerts.Count > 0
            ? alerts.Select(x => new { x.AlertType, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
            : _store.Alerts.Select(x => new { x.AlertType, x.CreatedAt }).ToList();

        var today = DateOnly.FromDateTime(DateTime.Now);
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
