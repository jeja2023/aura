using Aura.Api.Data;
using Aura.Api.Models;

namespace Aura.Api.Capture;

internal sealed class CaptureOpsService
{
    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly CaptureRepository _captureRepository;
    private readonly MonitoringRepository _monitoringRepository;
    private readonly AuditRepository _auditRepository;

    public CaptureOpsService(
        AppStore store,
        PgSqlConnectionFactory pgSqlConnectionFactory,
        CaptureRepository captureRepository,
        MonitoringRepository monitoringRepository,
        AuditRepository auditRepository)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _captureRepository = captureRepository;
        _monitoringRepository = monitoringRepository;
        _auditRepository = auditRepository;
    }

    public async Task<IResult> CreateMockAsync(CaptureMockReq req)
    {
        var record = new CaptureEntity(
            Interlocked.Increment(ref _store.CaptureSeed),
            req.DeviceId,
            req.ChannelNo,
            DateTimeOffset.Now,
            req.MetadataJson);

        var dbId = await _captureRepository.InsertCaptureAsync(
            record.DeviceId,
            record.ChannelNo,
            record.CaptureTime,
            record.MetadataJson,
            null);

        var saved = dbId.HasValue ? record with { CaptureId = dbId.Value } : record;
        if (!dbId.HasValue)
        {
            _store.Captures.Add(saved);
        }

        await _auditRepository.InsertOperationAsync("楼栋管理员", "模拟抓拍", $"设备={req.DeviceId}, 通道={req.ChannelNo}");
        AddOperationLog("楼栋管理员", "模拟抓拍", $"设备={req.DeviceId}, 通道={req.ChannelNo}");

        if (!string.IsNullOrWhiteSpace(req.MetadataJson) && req.MetadataJson.Contains("异常", StringComparison.Ordinal))
        {
            var alert = new AlertEntity(
                Interlocked.Increment(ref _store.AlertSeed),
                "异常滞留",
                $"模拟抓拍{saved.CaptureId}命中异常关键字",
                DateTimeOffset.Now);

            var alertId = await _monitoringRepository.InsertAlertAsync(alert.AlertType, alert.Detail);
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
            if (!string.IsNullOrWhiteSpace(fromQ) && DateTimeOffset.TryParse(fromQ, out var parsedFrom))
            {
                from = parsedFrom;
            }

            if (!string.IsNullOrWhiteSpace(toQ) && DateTimeOffset.TryParse(toQ, out var parsedTo))
            {
                to = parsedTo;
            }

            var (dbRows, total) = await _captureRepository.GetCapturesPagedAsync(from, to, pageNum, pageSize);
            if (_pgSqlConnectionFactory.IsConfigured)
            {
                var mapped = dbRows.Select(x => new CaptureEntity(
                    x.CaptureId,
                    x.DeviceId,
                    x.ChannelNo,
                    x.CaptureTime,
                    x.MetadataJson,
                    x.ImagePath));

                return Results.Ok(new { code = 0, msg = "查询成功", data = mapped, pagination = new { total, page = pageNum, pageSize } });
            }

            IEnumerable<CaptureEntity> mem = _store.Captures;
            if (from.HasValue)
            {
                mem = mem.Where(x => x.CaptureTime >= from.Value);
            }

            if (to.HasValue)
            {
                mem = mem.Where(x => x.CaptureTime <= to.Value);
            }

            var ordered = mem.OrderByDescending(x => x.CaptureId).ToList();
            var memTotal = ordered.Count;
            var slice = ordered.Skip((pageNum - 1) * pageSize).Take(pageSize).ToList();
            return Results.Ok(new { code = 0, msg = "查询成功", data = slice, pagination = new { total = memTotal, page = pageNum, pageSize } });
        }

        var limitStr = httpReq.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var parsedLimit) ? parsedLimit : defaultLimit;
        limit = Math.Clamp(limit, 1, maxLimit);

        var rows = await _captureRepository.GetCapturesAsync(limit);
        if (_pgSqlConnectionFactory.IsConfigured)
        {
            var mapped = rows.Select(x => new CaptureEntity(
                x.CaptureId,
                x.DeviceId,
                x.ChannelNo,
                x.CaptureTime,
                x.MetadataJson,
                x.ImagePath));

            return Results.Ok(new { code = 0, msg = "查询成功", data = mapped });
        }

        return Results.Ok(new
        {
            code = 0,
            msg = "查询成功",
            data = _store.Captures.OrderByDescending(x => x.CaptureId).Take(limit)
        });
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
