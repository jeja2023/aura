using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class PgSqlStore
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<PgSqlStore>? _logger;

    public PgSqlStore(PgSqlConnectionFactory connectionFactory, ILogger<PgSqlStore>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<bool> TryPingAsync(CancellationToken cancellationToken = default)
    {
        if (!_connectionFactory.IsConfigured)
        {
            _logger?.LogWarning("PostgreSQL 连接串未配置，就绪探测失败。");
            return false;
        }

        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            return scalar is not null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PostgreSQL 就绪探测失败。");
            return false;
        }
    }
}
