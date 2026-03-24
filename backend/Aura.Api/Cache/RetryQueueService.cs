/* 文件：重试队列服务（RetryQueueService.cs） | File: Retry Queue Service */
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Aura.Api.Cache;

internal sealed class RetryQueueService
{
    private readonly IDatabase? _db;
    private readonly ILogger<RetryQueueService> _logger;
    private const string QueueKey = "aura:retry:capture";

    public RetryQueueService(string? connectionString, ILogger<RetryQueueService> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Redis 重试队列未启用：连接串为空。");
            return;
        }
        try
        {
            var mux = ConnectionMultiplexer.Connect(connectionString);
            _db = mux.GetDatabase();
        }
        catch (Exception ex)
        {
            _db = null;
            _logger.LogError(ex, "Redis 重试队列初始化失败，已降级为禁用状态。");
        }
    }

    public bool Enabled => _db is not null;

    public async Task EnqueueAsync(RetryTask task)
    {
        if (_db is null)
        {
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(task);
            await _db.ListRightPushAsync(QueueKey, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试任务入队失败。captureId={CaptureId}, deviceId={DeviceId}, retry={RetryCount}", task.CaptureId, task.DeviceId, task.RetryCount);
        }
    }

    public async Task<RetryTask?> DequeueAsync()
    {
        if (_db is null)
        {
            return null;
        }
        try
        {
            var value = await _db.ListLeftPopAsync(QueueKey);
            if (!value.HasValue)
            {
                return null;
            }
            return JsonSerializer.Deserialize<RetryTask>(value.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试任务出队失败。");
            return null;
        }
    }

    public async Task<long> LengthAsync()
    {
        if (_db is null)
        {
            return 0;
        }
        try
        {
            return await _db.ListLengthAsync(QueueKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询重试队列长度失败。");
            return 0;
        }
    }
}

internal sealed record RetryTask(
    long CaptureId,
    long DeviceId,
    int ChannelNo,
    string? ImagePath,
    string? ImageBase64,
    string MetadataJson,
    string Source,
    int RetryCount,
    DateTimeOffset CreatedAt);
