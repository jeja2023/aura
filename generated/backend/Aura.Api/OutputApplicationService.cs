using Aura.Api.Data;
using Aura.Api.Internal;

internal sealed class OutputApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly CaptureRepository _captureRepository;
    private readonly MonitoringRepository _monitoringRepository;

    public OutputApplicationService(
        AppStore store,
        PgSqlConnectionFactory pgSqlConnectionFactory,
        CaptureRepository captureRepository,
        MonitoringRepository monitoringRepository)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _captureRepository = captureRepository;
        _monitoringRepository = monitoringRepository;
    }

    public async Task<IResult> GetEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 200;
        if (pageSize > 1000) pageSize = 1000;

        var dbResult = await _captureRepository.GetCapturesPagedAsync(from, to, page, pageSize);
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            if (!dbResult.Succeeded)
            {
                return AuraApiResults.ServiceUnavailable("数据库查询失败，无法获取输出事件", 50311);
            }

            var data = dbResult.Rows.Select(x => new
            {
                eventType = "capture",
                captureId = x.CaptureId,
                x.DeviceId,
                x.ChannelNo,
                captureTime = x.CaptureTime,
                metadata = x.MetadataJson
            });
            return Results.Ok(new { code = 0, msg = "查询成功", data, pager = new { page, pageSize, total = dbResult.Total } });
        }

        var dataMem = _store.Captures
            .Where(x => !from.HasValue || x.CaptureTime >= from.Value)
            .Where(x => !to.HasValue || x.CaptureTime <= to.Value)
            .OrderByDescending(x => x.CaptureId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                eventType = "capture",
                captureId = x.CaptureId,
                x.DeviceId,
                x.ChannelNo,
                captureTime = x.CaptureTime,
                metadata = x.MetadataJson
            });
        var memTotal = _store.Captures.Count(x => (!from.HasValue || x.CaptureTime >= from.Value) && (!to.HasValue || x.CaptureTime <= to.Value));
        return Results.Ok(new { code = 0, msg = "查询成功", data = dataMem, pager = new { page, pageSize, total = memTotal } });
    }

    public async Task<IResult> GetPersonsAsync(int minCapture)
    {
        if (minCapture <= 0) minCapture = 1;
        var rows = await _monitoringRepository.GetVirtualPersonsAsync();
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            var dataDb = rows.Where(x => x.CaptureCount >= minCapture)
                .Select(x => new { vid = x.Vid, mainDevice = x.DeviceId, captureCount = x.CaptureCount, x.FirstSeen, x.LastSeen });
            return Results.Ok(new { code = 0, msg = "查询成功", data = dataDb });
        }

        var data = _store.Captures
            .GroupBy(x => x.DeviceId)
            .Select((g, i) => new { vid = $"V_DEMO_{i + 1:000}", mainDevice = g.Key, captureCount = g.Count() })
            .Where(x => x.captureCount >= minCapture);
        return Results.Ok(new { code = 0, msg = "查询成功", data });
    }
}



