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
