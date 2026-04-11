/* 文件：抓拍运维服务（CaptureOpsService.cs） | File: Capture Ops Service */
using Aura.Api.Models;
using Aura.Api.Data;

namespace Aura.Api.Capture;

internal sealed class CaptureOpsService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;

    public CaptureOpsService(AppStore store, PgSqlStore db)
    {
        _store = store;
        _db = db;
    }

    public async Task<IResult> CreateMockAsync(CaptureMockReq req)
    {
        var record = new CaptureEntity(Interlocked.Increment(ref _store.CaptureSeed), req.DeviceId, req.ChannelNo, DateTimeOffset.Now, req.MetadataJson);
        var dbId = await _db.InsertCaptureAsync(record.DeviceId, record.ChannelNo, record.CaptureTime, record.MetadataJson, null);
        var saved = dbId.HasValue ? record with { CaptureId = dbId.Value } : record;
        if (!dbId.HasValue)
        {
            _store.Captures.Add(saved);
        }

        await _db.InsertOperationAsync("楼栋管理员", "模拟抓拍", $"设备={req.DeviceId}, 通道={req.ChannelNo}");
        AddOperationLog("楼栋管理员", "模拟抓拍", $"设备={req.DeviceId}, 通道={req.ChannelNo}");
        if (!string.IsNullOrWhiteSpace(req.MetadataJson) && req.MetadataJson.Contains("异常", StringComparison.Ordinal))
        {
            var alert = new AlertEntity(Interlocked.Increment(ref _store.AlertSeed), "异常滞留", $"模拟抓拍{saved.CaptureId}命中异常关键字", DateTimeOffset.Now);
            var alertId = await _db.InsertAlertAsync(alert.AlertType, alert.Detail);
            if (!alertId.HasValue)
            {
                _store.Alerts.Add(alert);
            }
        }

        return Results.Ok(new { code = 0, msg = "模拟抓拍创建成功", data = saved });
    }

    public async Task<IResult> GetCapturesAsync(HttpRequest httpReq)
    {
        const int defaultLimit = 500;
        const int maxLimit = 2000;
        const int maxPageSize = 200;

        if (int.TryParse(httpReq.Query["page"].FirstOrDefault(), out var pageNum) && pageNum > 0)
        {
            var pageSize = int.TryParse(httpReq.Query["pageSize"].FirstOrDefault(), out var ps) ? ps : 20;
            pageSize = Math.Clamp(pageSize, 1, maxPageSize);
            DateTimeOffset? from = null;
            DateTimeOffset? to = null;
            var fromQ = httpReq.Query["from"].FirstOrDefault();
            var toQ = httpReq.Query["to"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fromQ) && DateTimeOffset.TryParse(fromQ, out var f)) from = f;
            if (!string.IsNullOrWhiteSpace(toQ) && DateTimeOffset.TryParse(toQ, out var t)) to = t;

            var (dbRows, total) = await _db.GetCapturesPagedAsync(from, to, pageNum, pageSize);
            if (dbRows.Count > 0)
            {
                var mapped = dbRows.Select(x => new CaptureEntity(x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson, x.ImagePath));
                return Results.Ok(new { code = 0, msg = "查询成功", data = mapped, pagination = new { total, page = pageNum, pageSize } });
            }

            IEnumerable<CaptureEntity> mem = _store.Captures;
            if (from.HasValue) mem = mem.Where(x => x.CaptureTime >= from.Value);
            if (to.HasValue) mem = mem.Where(x => x.CaptureTime <= to.Value);
            var ordered = mem.OrderByDescending(x => x.CaptureId).ToList();
            var memTotal = ordered.Count;
            var slice = ordered.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
            return Results.Ok(new { code = 0, msg = "查询成功", data = slice, pagination = new { total = memTotal, page = pageNum, pageSize } });
        }

        var limitStr = httpReq.Query["limit"].FirstOrDefault();
        var lim = int.TryParse(limitStr, out var ll) ? ll : defaultLimit;
        lim = Math.Clamp(lim, 1, maxLimit);
        var rows = await _db.GetCapturesAsync(lim);
        if (rows.Count > 0)
        {
            var mapped = rows.Select(x => new CaptureEntity(x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson, x.ImagePath));
            return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
        }

        return Results.Ok(new { code = 0, msg = "查询成功", data = _store.Captures.OrderByDescending(x => x.CaptureId).Take(lim) });
    }

    private void AddOperationLog(string operatorName, string action, string detail)
    {
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: operatorName,
            Action: action,
            Detail: detail,
            CreatedAt: DateTimeOffset.Now));
    }
}
