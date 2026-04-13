/* 文件：告警通知器（AlertNotifier.cs） | File: Alert Notifier */
namespace Aura.Api.Ops;

internal sealed record AlertNotifyMessage(
    string AlertType,
    string Detail,
    string Source,
    DateTimeOffset OccurredAt);

internal sealed record AlertNotifyStats(
    long TotalNotify,
    long WebhookSuccess,
    long WebhookFailure,
    long FileSuccess,
    long FileFailure,
    string? LastFailureChannel,
    string? LastFailureReason,
    DateTimeOffset? LastFailureAt);

internal interface IAlertNotifier
{
    Task NotifyAsync(AlertNotifyMessage message, CancellationToken cancellationToken = default);
    AlertNotifyStats GetStats();
}

internal sealed class AlertNotifier : IAlertNotifier
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AlertNotifier> _logger;
    private readonly string? _webhookUrl;
    private readonly string? _filePath;
    private long _totalNotify;
    private long _webhookSuccess;
    private long _webhookFailure;
    private long _fileSuccess;
    private long _fileFailure;
    private readonly object _failureLock = new();
    private string? _lastFailureChannel;
    private string? _lastFailureReason;
    private DateTimeOffset? _lastFailureAt;

    public AlertNotifier(HttpClient httpClient, ILogger<AlertNotifier> logger, string? webhookUrl, string? filePath)
    {
        _httpClient = httpClient;
        _logger = logger;
        _webhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim();
    }

    public async Task NotifyAsync(AlertNotifyMessage message, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _totalNotify);
        await NotifyWebhookAsync(message, cancellationToken);
        await NotifyFileAsync(message, cancellationToken);
    }

    public AlertNotifyStats GetStats()
    {
        lock (_failureLock)
        {
            return new AlertNotifyStats(
                Interlocked.Read(ref _totalNotify),
                Interlocked.Read(ref _webhookSuccess),
                Interlocked.Read(ref _webhookFailure),
                Interlocked.Read(ref _fileSuccess),
                Interlocked.Read(ref _fileFailure),
                _lastFailureChannel,
                _lastFailureReason,
                _lastFailureAt);
        }
    }

    private async Task NotifyWebhookAsync(AlertNotifyMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webhookUrl)) return;
        try
        {
            var resp = await _httpClient.PostAsJsonAsync(_webhookUrl, new
            {
                alertType = message.AlertType,
                detail = message.Detail,
                source = message.Source,
                occurredAt = message.OccurredAt
            }, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _webhookFailure);
                MarkFailure("webhook", $"HTTP {(int)resp.StatusCode}");
                _logger.LogWarning("告警通知 Webhook 返回非成功状态。status={StatusCode}, alertType={AlertType}", (int)resp.StatusCode, message.AlertType);
                return;
            }
            Interlocked.Increment(ref _webhookSuccess);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _webhookFailure);
            MarkFailure("webhook", ex.Message);
            _logger.LogError(ex, "告警通知 Webhook 调用失败。alertType={AlertType}", message.AlertType);
        }
    }

    private async Task NotifyFileAsync(AlertNotifyMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_filePath)) return;
        /* 禁止相对路径：Path.GetFullPath 会相对进程 CWD，易在 backend/Aura.Api 下误建 storage */
        if (!Path.IsPathRooted(_filePath))
        {
            _logger.LogWarning("告警文件路径必须为绝对路径（请经 ProjectPaths.ResolvePathRelativeToProjectRoot 解析）。path={Path}", _filePath);
            return;
        }

        try
        {
            /* 路径应在启动时由 ProjectPaths.ResolvePathRelativeToProjectRoot 解析为绝对路径，勿依赖 CWD */
            var path = Path.GetFullPath(_filePath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\t{message.AlertType}\t{message.Source}\t{message.Detail}{Environment.NewLine}";
            await File.AppendAllTextAsync(path, line, cancellationToken);
            Interlocked.Increment(ref _fileSuccess);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _fileFailure);
            MarkFailure("file", ex.Message);
            _logger.LogError(ex, "告警通知文件写入失败。alertType={AlertType}", message.AlertType);
        }
    }

    private void MarkFailure(string channel, string reason)
    {
        lock (_failureLock)
        {
            _lastFailureChannel = channel;
            _lastFailureReason = reason;
            _lastFailureAt = DateTimeOffset.Now;
        }
    }
}
