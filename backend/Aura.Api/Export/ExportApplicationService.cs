using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;

namespace Aura.Api.Export;

internal sealed class ExportApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly CaptureRepository _captureRepository;
    private readonly MonitoringRepository _monitoringRepository;
    private readonly AuditRepository _auditRepository;
    private readonly UserAuthRepository _userAuthRepository;
    private readonly TabularExportService _tabularExportService;
    private readonly string _storageRoot;

    public ExportApplicationService(
        AppStore store,
        PgSqlConnectionFactory pgSqlConnectionFactory,
        CaptureRepository captureRepository,
        MonitoringRepository monitoringRepository,
        AuditRepository auditRepository,
        UserAuthRepository userAuthRepository,
        TabularExportService tabularExportService,
        string storageRoot)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _captureRepository = captureRepository;
        _monitoringRepository = monitoringRepository;
        _auditRepository = auditRepository;
        _userAuthRepository = userAuthRepository;
        _tabularExportService = tabularExportService;
        _storageRoot = storageRoot;
    }

    public async Task<IResult> ExportAsync(string type, string dataset, int maxRows, string? keyword = null)
    {
        type = type.Trim().ToLowerInvariant();
        dataset = NormalizeDataset(dataset);
        if (type is not ("csv" or "xlsx"))
        {
            return AuraApiResults.BadRequest("仅支持 csv/xlsx", 40061);
        }

        if (dataset is not ("capture" or "alert" or "judge" or "operation" or "system" or "user"))
        {
            return AuraApiResults.BadRequest("dataset 仅支持 capture/alert/judge/operation/system/user", 40062);
        }

        if (maxRows <= 0)
        {
            maxRows = 5000;
        }

        maxRows = Math.Min(maxRows, 20000);
        var useDb = _pgSqlConnectionFactory.IsConfigured;

        List<string[]> rows;
        if (dataset == "capture")
        {
            var captures = await _captureRepository.GetCapturesAsync(maxRows);
            var source = useDb
                ? captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, CaptureTime = new DateTimeOffset(x.CaptureTime), x.MetadataJson }).ToList()
                : captures.Count > 0
                    ? captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, CaptureTime = new DateTimeOffset(x.CaptureTime), x.MetadataJson }).ToList()
                    : _store.Captures.Select(x => new { x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson }).ToList();

            rows =
            [
                ["抓拍ID", "设备ID", "通道号", "抓拍时间", "元数据"],
                .. source.Select(x => new[]
                {
                    $"{x.CaptureId}",
                    $"{x.DeviceId}",
                    $"{x.ChannelNo}",
                    x.CaptureTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    EscapeCell(x.MetadataJson)
                })
            ];
        }
        else if (dataset == "alert")
        {
            var alerts = await _monitoringRepository.GetAlertsAsync(maxRows);
            var source = useDb
                ? alerts.Select(x => new { x.AlertId, x.AlertType, x.Detail, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
                : alerts.Count > 0
                    ? alerts.Select(x => new { x.AlertId, x.AlertType, x.Detail, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
                    : _store.Alerts.Select(x => new { x.AlertId, x.AlertType, Detail = x.Detail, x.CreatedAt }).ToList();

            rows =
            [
                ["告警ID", "告警类型", "详情", "创建时间"],
                .. source.Select(x => new[]
                {
                    $"{x.AlertId}",
                    x.AlertType,
                    EscapeCell(x.Detail),
                    x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                })
            ];
        }
        else if (dataset == "judge")
        {
            var date = DateOnly.FromDateTime(DateTime.Now);
            var judgeRows = await _monitoringRepository.GetJudgeResultsAsync(date, null, maxRows);
            var source = useDb
                ? judgeRows.Select(x => new { x.JudgeId, x.Vid, x.RoomId, x.JudgeType, x.JudgeDate, x.DetailJson }).ToList()
                : judgeRows.Count > 0
                    ? judgeRows.Select(x => new { x.JudgeId, x.Vid, x.RoomId, x.JudgeType, x.JudgeDate, x.DetailJson }).ToList()
                    : _store.JudgeResults.Select(x => new
                    {
                        x.JudgeId,
                        x.Vid,
                        x.RoomId,
                        x.JudgeType,
                        JudgeDate = x.JudgeDate.ToDateTime(TimeOnly.MinValue),
                        x.DetailJson
                    }).ToList();

            rows =
            [
                ["研判ID", "人员虚拟编号", "房间ID", "研判类型", "研判日期", "详情数据"],
                .. source.Select(x => new[]
                {
                    $"{x.JudgeId}",
                    x.Vid,
                    $"{x.RoomId}",
                    x.JudgeType,
                    x.JudgeDate.ToString("yyyy-MM-dd"),
                    EscapeCell(x.DetailJson)
                })
            ];
        }
        else if (dataset == "operation")
        {
            var dbResult = await _auditRepository.GetOperationsAsync(keyword, 1, maxRows);
            var source = useDb
                ? dbResult.Rows.Select(x => new { x.OperationId, x.OperatorName, x.Action, x.Detail, x.CreatedAt }).ToList()
                : (dbResult.Total > 0 || dbResult.Rows.Count > 0)
                    ? dbResult.Rows.Select(x => new { x.OperationId, x.OperatorName, x.Action, x.Detail, x.CreatedAt }).ToList()
                    : _store.Operations
                        .Where(x => string.IsNullOrWhiteSpace(keyword)
                            || x.Action.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || x.OperatorName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.OperationId)
                        .Take(maxRows)
                        .Select(x => new { x.OperationId, x.OperatorName, x.Action, Detail = x.Detail, CreatedAt = x.CreatedAt.DateTime })
                        .ToList();

            rows =
            [
                ["操作ID", "操作员", "动作", "详情", "创建时间"],
                .. source.Select(x => new[]
                {
                    $"{x.OperationId}",
                    x.OperatorName,
                    x.Action,
                    EscapeCell(x.Detail),
                    x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                })
            ];
        }
        else if (dataset == "system")
        {
            var dbResult = await _auditRepository.GetSystemLogsAsync(keyword, 1, maxRows);
            var source = useDb
                ? dbResult.Rows.Select(x => new { x.SystemLogId, x.Level, x.Source, x.Message, x.CreatedAt }).ToList()
                : (dbResult.Total > 0 || dbResult.Rows.Count > 0)
                    ? dbResult.Rows.Select(x => new { x.SystemLogId, x.Level, x.Source, x.Message, x.CreatedAt }).ToList()
                    : _store.SystemLogs
                        .Where(x => string.IsNullOrWhiteSpace(keyword)
                            || x.Level.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || x.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || x.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.SystemLogId)
                        .Take(maxRows)
                        .Select(x => new { x.SystemLogId, x.Level, x.Source, Message = x.Message, CreatedAt = x.CreatedAt.DateTime })
                        .ToList();

            rows =
            [
                ["系统日志ID", "级别", "来源", "消息", "创建时间"],
                .. source.Select(x => new[]
                {
                    $"{x.SystemLogId}",
                    x.Level,
                    x.Source,
                    EscapeCell(x.Message),
                    x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                })
            ];
        }
        else
        {
            var dbUsers = await _userAuthRepository.GetUsersAsync();
            var source = useDb
                ? dbUsers.Select(x => new
                {
                    x.UserId,
                    x.UserName,
                    x.DisplayName,
                    x.RoleId,
                    x.RoleName,
                    x.Status,
                    x.CreatedAt,
                    x.LastLoginAt
                }).ToList()
                : dbUsers.Count > 0
                    ? dbUsers.Select(x => new
                    {
                        x.UserId,
                        x.UserName,
                        x.DisplayName,
                        x.RoleId,
                        x.RoleName,
                        x.Status,
                        x.CreatedAt,
                        x.LastLoginAt
                    }).ToList()
                    : _store.Users.Select(x => new
                    {
                        x.UserId,
                        x.UserName,
                        x.DisplayName,
                        x.RoleId,
                        RoleName = (string?)x.RoleName,
                        Status = (long)x.Status,
                        CreatedAt = x.CreatedAt.DateTime,
                        LastLoginAt = x.LastLoginAt?.DateTime
                    }).ToList();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                source = source.Where(x =>
                        x.UserName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || x.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var ordered = source
                .OrderByDescending(x => x.UserId)
                .Take(maxRows)
                .ToList();

            rows =
            [
                ["用户ID", "用户名", "昵称", "角色", "状态", "创建时间", "最后登录时间"],
                .. ordered.Select(x => new[]
                {
                    $"{x.UserId}",
                    x.UserName,
                    x.DisplayName,
                    ResolveRoleLabel(x.RoleId, x.RoleName),
                    x.Status == 1 ? "启用" : "禁用",
                    x.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    x.LastLoginAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty
                })
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

        await _auditRepository.InsertOperationAsync("楼栋管理员", "报表导出", $"type={type}, dataset={dataset}, file={fileName}");
        var downloadUrl = $"/storage/outputs/{Uri.EscapeDataString(fileName)}";
        return Results.Ok(new { code = 0, msg = "导出文件已生成", data = new { fileName, downloadUrl, type, dataset } });
    }

    private static string ExportDatasetTitleCn(string dataset) => dataset switch
    {
        "capture" => "抓拍记录",
        "alert" => "告警记录",
        "judge" => "研判记录",
        "operation" => "操作日志",
        "system" => "系统日志",
        "user" => "用户列表",
        _ => "数据导出"
    };

    private static string ResolveRoleLabel(long roleId, string? roleName)
    {
        if (roleId == 1)
        {
            return "管理员";
        }

        if (roleId == 2)
        {
            return "普通用户";
        }

        if (!string.IsNullOrWhiteSpace(roleName))
        {
            return roleName;
        }

        return "未知角色";
    }

    private static string NormalizeDataset(string? dataset)
    {
        var value = (dataset ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "log" or "logs" or "operationlog" or "operation-log" => "operation",
            "systemlog" or "system-log" => "system",
            "users" or "userinfo" or "userlist" or "user-list" => "user",
            _ => value
        };
    }

    private static string EscapeCell(string? text)
    {
        return text?.Replace("\r", " ").Replace("\n", " ") ?? string.Empty;
    }
}
