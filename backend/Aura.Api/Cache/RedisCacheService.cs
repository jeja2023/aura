/* 文件：Redis缓存服务（RedisCacheService.cs） */
using StackExchange.Redis;

namespace Aura.Api.Cache;

internal sealed class RedisCacheService
{
    private readonly RedisConnectionProvider _provider;
    private readonly IDatabase? _db;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(RedisConnectionProvider provider, ILogger<RedisCacheService> logger)
    {
        _provider = provider;
        _db = provider.Database;
        _logger = logger;
    }

    public bool Enabled => _provider.Enabled;

    public async Task<long?> TryConsumeFixedWindowAsync(string key, TimeSpan window, long limit)
    {
        if (_db is null) return null;
        if (limit <= 0) return null;

        try
        {
            var count = await _db.StringIncrementAsync(key);
            if (count == 1)
            {
                await _db.KeyExpireAsync(key, window);
            }
            return count;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "fixed-window-rate-limit");
            return null;
        }
    }

    public async Task<string?> GetAsync(string key)
    {
        if (_db is null)
        {
            return null;
        }

        try
        {
            var value = await _db.StringGetAsync(key);
            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "cache-get");
            return null;
        }
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl)
    {
        if (_db is null)
        {
            return;
        }

        try
        {
            await _db.StringSetAsync(key, value, ttl);
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "cache-set");
        }
    }

    public async Task<bool?> TryAddAsync(string key, string value, TimeSpan ttl)
    {
        if (_db is null)
        {
            return null;
        }

        try
        {
            return await _db.StringSetAsync(key, value, ttl, When.NotExists);
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "cache-add-if-absent");
            return null;
        }
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
            _logger.LogWarning(ex, "缓存删除失败。key={Key}", key);
        }
    }

    public async Task<string?> TryAcquireLockAsync(string lockKey, TimeSpan ttl)
    {
        if (_db is null) return null;
        try
        {
            var token = Guid.NewGuid().ToString("N");
            var ok = await _db.StringSetAsync(lockKey, token, ttl, When.NotExists);
            return ok ? token : null;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "lock-acquire");
            return null;
        }
    }

    public async Task ReleaseLockAsync(string lockKey, string token)
    {
        if (_db is null) return;
        try
        {
            const string releaseScript = """
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('del', KEYS[1])
                end
                return 0
                """;
            await _db.ScriptEvaluateAsync(
                releaseScript,
                [new RedisKey(lockKey)],
                [new RedisValue(token)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 Redis 锁失败。lockKey={LockKey}", lockKey);
        }
    }

    public async Task<bool> RenewLockAsync(string lockKey, string token, TimeSpan ttl)
    {
        if (_db is null) return false;
        try
        {
            const string renewScript = """
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('pexpire', KEYS[1], ARGV[2])
                end
                return 0
                """;
            var result = await _db.ScriptEvaluateAsync(
                renewScript,
                [new RedisKey(lockKey)],
                [new RedisValue(token), new RedisValue(((long)Math.Ceiling(ttl.TotalMilliseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            return (long)result == 1;
        }
        catch (Exception ex)
        {
            _provider.RecordFailure(ex, "lock-renew");
            return false;
        }
    }
}
