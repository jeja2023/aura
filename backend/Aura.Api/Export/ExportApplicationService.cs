using Aura.Api.Models;
using Aura.Api.Data;

namespace Aura.Api.Export;

internal sealed class ExportApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;
    private readonly TabularExportService _tabularExportService;
    private readonly string _storageRoot;

    public ExportApplicationService(AppStore store, PgSqlStore db, TabularExportService tabularExportService, string storageRoot)
    {
        _store = store;
        _db = db;
        _tabularExportService = tabularExportService;
        _storageRoot = storageRoot;
    }

    public async Task<IResult> ExportAsync(string type, string dataset, int maxRows)
    {
        type = type.Trim().ToLowerInvariant();
        dataset = dataset.Trim().ToLowerInvariant();
        if (type is not ("csv" or "xlsx"))
        {
            return Results.BadRequest(new { code = 40061, msg = "仅支持 csv/xlsx" });
        }
        if (dataset is not ("capture" or "alert" or "judge"))
        {
            return Results.BadRequest(new { code = 40062, msg = "dataset 仅支持 capture/alert/judge" });
        }
        if (maxRows <= 0) maxRows = 5000;
        maxRows = Math.Min(maxRows, 20000);

        List<string[]> rows;
        if (dataset == "capture")
        {
            var captures = await _db.GetCapturesAsync(maxRows);
            var source = captures.Count > 0
                ? captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, CaptureTime = new DateTimeOffset(x.CaptureTime), x.MetadataJson }).ToList()
                : _store.Captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson }).ToList();
            rows =
            [
                ["抓拍ID", "设备ID", "通道号", "抓拍时间", "元数据"],
                ..source.Select(x => new[] { $"{x.CaptureId}", $"{x.DeviceId}", $"{x.ChannelNo}", x.CaptureTime.ToString("yyyy-MM-dd HH:mm:ss"), EscapeCell(x.MetadataJson) })
            ];
        }
        else if (dataset == "alert")
        {
            var alerts = await _db.GetAlertsAsync(maxRows);
            var source = alerts.Count > 0
                ? alerts.Select(x => new { x.AlertId, x.AlertType, x.Detail, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
                : _store.Alerts.Select(x => new { x.AlertId, x.AlertType, Detail = x.Detail, x.CreatedAt }).ToList();
            rows =
            [
                ["告警ID", "告警类型", "详情", "创建时间"],
                ..source.Select(x => new[] { $"{x.AlertId}", x.AlertType, EscapeCell(x.Detail), x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") })
            ];
        }
        else
        {
            var date = DateOnly.FromDateTime(DateTime.Now);
            var judgeRows = await _db.GetJudgeResultsAsync(date, null, maxRows);
            var source = judgeRows.Count > 0
                ? judgeRows.Select(x => new { x.JudgeId, x.Vid, x.RoomId, x.JudgeType, x.JudgeDate, x.DetailJson }).ToList()
                : _store.JudgeResults.Select(x => new { x.JudgeId, x.Vid, x.RoomId, x.JudgeType, JudgeDate = x.JudgeDate.ToDateTime(TimeOnly.MinValue), x.DetailJson }).ToList();
            rows =
            [
                ["研判ID", "人员虚拟编号", "房间ID", "研判类型", "研判日期", "详情数据"],
                ..source.Select(x => new[] { $"{x.JudgeId}", x.Vid, $"{x.RoomId}", x.JudgeType, x.JudgeDate.ToString("yyyy-MM-dd"), EscapeCell(x.DetailJson) })
            ];
        }

        var ext = type == "xlsx" ? "xlsx" : "csv";
        var titleCn = ExportDatasetTitleCn(dataset);
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var fileName = $"{titleCn}_{DateTimeOffset.Now:yyyyMMddHHmmss}_{shortId}.{ext}";
        var exportDir = Path.Combine(_storageRoot, "outputs");
        Directory.CreateDirectory(exportDir);
        var localPath = Path.Combine(exportDir, fileName);
        if (type == "csv")
        {
            await _tabularExportService.WriteCsvAsync(localPath, rows);
        }
        else
        {
            await _tabularExportService.WriteXlsxAsync(localPath, titleCn, rows);
        }

        await _db.InsertOperationAsync("楼栋管理员", "报表导出", $"type={type}, dataset={dataset}, file={fileName}");
        var downloadUrl = $"/storage/outputs/{Uri.EscapeDataString(fileName)}";
        return Results.Ok(new { code = 0, msg = "导出文件已生成", data = new { fileName, downloadUrl, type, dataset } });
    }

    private static string ExportDatasetTitleCn(string dataset) => dataset switch
    {
        "capture" => "抓拍记录",
        "alert" => "告警记录",
        "judge" => "研判记录",
        _ => "数据导出"
    };

    private static string EscapeCell(string? text)
    {
        return text?.Replace("\r", " ").Replace("\n", " ") ?? "";
    }
}

