using Aura.Api.Data;

internal sealed class OutputApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;

    internal sealed record OutputEventsResult(object Data, object Pager);
    internal sealed record OutputPersonsResult(object Data);

    public OutputApplicationService(AppStore store, PgSqlStore db)
    {
        _store = store;
        _db = db;
    }

    public async Task<OutputEventsResult> GetEventsAsync(DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 200;
        if (pageSize > 1000) pageSize = 1000;

        var (rows, total) = await _db.GetCapturesPagedAsync(from, to, page, pageSize);
        var data = rows.Select(x => new
        {
            eventType = "capture",
            captureId = x.CaptureId,
            x.DeviceId,
            x.ChannelNo,
            captureTime = x.CaptureTime,
            metadata = x.MetadataJson
        });
        return new OutputEventsResult(data, new { page, pageSize, total });
    }

    public async Task<OutputPersonsResult> GetPersonsAsync(int minCapture)
    {
        if (minCapture <= 0) minCapture = 1;
        var rows = await _db.GetVirtualPersonsAsync();
        if (rows.Count > 0)
        {
            var dataDb = rows.Where(x => x.CaptureCount >= minCapture)
                .Select(x => new { vid = x.Vid, mainDevice = x.DeviceId, captureCount = x.CaptureCount, x.FirstSeen, x.LastSeen });
            return new OutputPersonsResult(dataDb);
        }

        var data = _store.Captures
            .GroupBy(x => x.DeviceId)
            .Select((g, i) => new { vid = $"V_DEMO_{i + 1:000}", mainDevice = g.Key, captureCount = g.Count() })
            .Where(x => x.captureCount >= minCapture);
        return new OutputPersonsResult(data);
    }
}
