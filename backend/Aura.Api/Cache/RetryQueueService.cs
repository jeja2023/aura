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
    private const string ProcessingKey = "aura:retry:capture:processing";

    public RetryQueueService(RedisConnectionProvider provider, ILogger<RetryQueueService> logger)
    {
        _provider = provider;
        _db = provider.Database;
        _logger = logger;
    }

    public bool Enabled => _provider.Enabled;

    public async Task<bool> EnqueueAsync(RetryTask task)
    {
        if (_db is null)
        {
            return false;
        }
        try
        {
            var json = JsonSerializer.Serialize(task, AuraJsonSerializerOptions.Default);
            await _db.ListRightPushAsync(QueueKey, json);
            return true;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-enqueue");
            _logger.LogError(ex, "重试任务入队失败。captureId={CaptureId}, deviceId={DeviceId}, retry={RetryCount}", task.CaptureId, task.DeviceId, task.RetryCount);
            return false;
        }
    }

    public async Task<RetryQueueItem?> DequeueAsync()
    {
        if (_db is null)
        {
            return null;
        }
        try
        {
            const string reserveScript = """
                local value = redis.call('lpop', KEYS[1])
                if value then redis.call('rpush', KEYS[2], value) end
                return value
                """;
            var result = await _db.ScriptEvaluateAsync(
                reserveScript,
                [new RedisKey(QueueKey), new RedisKey(ProcessingKey)]);
            if (result.IsNull)
            {
                return null;
            }
            var receipt = result.ToString();
            var task = JsonSerializer.Deserialize<RetryTask>(receipt, AuraJsonSerializerOptions.Default);
            if (task is null)
            {
                await _db.ListRemoveAsync(ProcessingKey, receipt, 1);
                _logger.LogError("丢弃无法反序列化的重试任务。");
                return null;
            }
            return new RetryQueueItem(task, receipt);
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-dequeue");
            _logger.LogError(ex, "重试任务出队失败。");
            return null;
        }
    }

    public async Task<bool> AckAsync(RetryQueueItem item)
    {
        if (_db is null) return false;
        try
        {
            return await _db.ListRemoveAsync(ProcessingKey, item.Receipt, 1) == 1;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-ack");
            return false;
        }
    }

    public async Task<bool> RequeueAsync(RetryQueueItem item, RetryTask nextTask)
    {
        if (_db is null) return false;
        try
        {
            const string requeueScript = """
                if redis.call('lrem', KEYS[1], 1, ARGV[1]) == 1 then
                    redis.call('rpush', KEYS[2], ARGV[2])
                    return 1
                end
                return 0
                """;
            var next = JsonSerializer.Serialize(nextTask, AuraJsonSerializerOptions.Default);
            var result = await _db.ScriptEvaluateAsync(
                requeueScript,
                [new RedisKey(ProcessingKey), new RedisKey(QueueKey)],
                [new RedisValue(item.Receipt), new RedisValue(next)]);
            return (long)result == 1;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-requeue");
            return false;
        }
    }

    public async Task<long> RecoverProcessingAsync(int limit = 1000)
    {
        if (_db is null) return 0;
        try
        {
            const string recoverScript = """
                local moved = 0
                while moved < tonumber(ARGV[1]) do
                    local value = redis.call('rpop', KEYS[1])
                    if not value then break end
                    redis.call('lpush', KEYS[2], value)
                    moved = moved + 1
                end
                return moved
                """;
            var result = await _db.ScriptEvaluateAsync(
                recoverScript,
                [new RedisKey(ProcessingKey), new RedisKey(QueueKey)],
                [new RedisValue(Math.Clamp(limit, 1, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            return (long)result;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "retry-recover");
            return 0;
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
            var pending = await _db.ListLengthAsync(QueueKey);
            var processing = await _db.ListLengthAsync(ProcessingKey);
            return pending + processing;
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

internal sealed record RetryQueueItem(RetryTask Task, string Receipt);
