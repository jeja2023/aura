/* 文件：重试队列服务（RetryQueueService.cs） */
using System.Text.Json;
using Aura.Api.Serialization;
using StackExchange.Redis;

namespace Aura.Api.Cache;

internal sealed class RetryQueueService
{
    private readonly RedisConnectionProvider _provider;
    private readonly IDatabase? _db;
    private readonly ILogger<RetryQueueService> _logger;
    private const string QueueKey = "aura:retry:capture";

    public RetryQueueService(RedisConnectionProvider provider, ILogger<RetryQueueService> logger)
    {
        _provider = provider;
        _db = provider.Database;
        _logger = logger;
    }

    public bool Enabled => _provider.Enabled;

    public async Task EnqueueAsync(RetryTask task)
    {
        if (_db is null)
        {
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(task, AuraJsonSerializerOptions.Default);
            await _db.ListRightPushAsync(QueueKey, json);
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-enqueue");
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
            return JsonSerializer.Deserialize<RetryTask>(value.ToString(), AuraJsonSerializerOptions.Default);
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-dequeue");
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
            _provider.RecordFailure(ex, "retry-length");
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
