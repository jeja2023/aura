using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Cache;

internal sealed class RedisCacheService
{
    private readonly IDatabase? _db;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(string? connectionString, ILogger<RedisCacheService> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Redis 缓存未启用：连接串为空。");
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
            _logger.LogError(ex, "Redis 缓存初始化失败，已降级为禁用状态。");
        }
    }

    public bool Enabled => _db is not null;

    public async Task<long?> TryConsumeFixedWindowAsync(string key, TimeSpan window, long limit)
    {
        if (_db is null) return null;
        if (limit <= 0) return null;

        // 固定窗口计数：第一次访问设置 TTL；超过 limit 返回当前计数（由调用方判断是否拒绝）
        var count = await _db.StringIncrementAsync(key);
        if (count == 1)
        {
            await _db.KeyExpireAsync(key, window);
        }
        return count;
    }

    public async Task<string?> GetAsync(string key)
    {
        if (_db is null)
        {
            return null;
        }
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl)
    {
        if (_db is null)
        {
            return;
        }
        await _db.StringSetAsync(key, value, ttl);
    }

    /// <summary>删除缓存键（设备列表等变更后主动失效）。</summary>
    public async Task DeleteAsync(string key)
    {
        if (_db is null)
        {
            return;
        }

        try
        {
            await _db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            // 删除失败不阻断主流程
            _logger.LogWarning(ex, "缓存删除失败。key={Key}", key);
        }
    }

    public async Task<string?> TryAcquireLockAsync(string lockKey, TimeSpan ttl)
    {
        if (_db is null) return null;
        var token = Guid.NewGuid().ToString("N");
        var ok = await _db.StringSetAsync(lockKey, token, ttl, When.NotExists);
        return ok ? token : null;
    }

    public async Task ReleaseLockAsync(string lockKey, string token)
    {
        if (_db is null) return;
        try
        {
            var current = await _db.StringGetAsync(lockKey);
            if (current.HasValue && current.ToString() == token)
            {
                await _db.KeyDeleteAsync(lockKey);
            }
        }
        catch (Exception ex)
        {
            // 释放锁失败不影响主流程；依赖 TTL 自动过期兜底
            _logger.LogWarning(ex, "释放 Redis 锁失败。lockKey={LockKey}", lockKey);
        }
    }
}
