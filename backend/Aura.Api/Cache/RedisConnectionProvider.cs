using StackExchange.Redis;

namespace Aura.Api.Cache;

internal sealed class RedisConnectionProvider : IDisposable
{
    private readonly ILogger<RedisConnectionProvider> _logger;
    private readonly ConnectionMultiplexer? _multiplexer;

    public RedisConnectionProvider(string? connectionString, ILogger<RedisConnectionProvider> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Redis 未启用：连接串为空。");
            return;
        }

        try
        {
            _multiplexer = ConnectionMultiplexer.Connect(connectionString);
            Database = _multiplexer.GetDatabase();
        }
        catch (Exception ex)
        {
            Database = null;
            LastError = ex.Message;
            _logger.LogError(ex, "Redis 初始化失败，相关能力将降级。");
        }
    }

    public IDatabase? Database { get; }
    public string? LastError { get; private set; }
    public bool Enabled => Database is not null;

    public async Task<long> DeleteByPatternsAsync(
        IReadOnlyCollection<string> patterns,
        int maxKeys,
        CancellationToken cancellationToken)
    {
        if (_multiplexer is null || Database is null)
            throw new InvalidOperationException("Redis is not configured.");

        var remaining = Math.Clamp(maxKeys, 1, 100_000);
        var deleted = 0L;
        foreach (var endpoint in _multiplexer.GetEndPoints())
        {
            var server = _multiplexer.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica) continue;
            foreach (var pattern in patterns.Distinct(StringComparer.Ordinal))
            {
                var batch = new List<RedisKey>(Math.Min(500, remaining));
                foreach (var key in server.Keys(Database.Database, pattern, pageSize: Math.Min(500, remaining)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    batch.Add(key);
                    if (batch.Count < 500 && batch.Count < remaining) continue;
                    deleted += await Database.KeyDeleteAsync(batch.ToArray());
                    remaining -= batch.Count;
                    batch.Clear();
                    if (remaining <= 0) return deleted;
                }
                if (batch.Count > 0)
                {
                    deleted += await Database.KeyDeleteAsync(batch.ToArray());
                    remaining -= batch.Count;
                    if (remaining <= 0) return deleted;
                }
            }
        }
        return deleted;
    }

    public void RecordFailure(Exception ex, string operation)
    {
        LastError = ex.Message;
        _logger.LogWarning(ex, "Redis 操作失败。operation={Operation}", operation);
    }

    public void Dispose()
    {
        _multiplexer?.Dispose();
    }
}
