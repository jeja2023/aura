using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal class SystemConfigRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<SystemConfigRepository>? _logger;

    public SystemConfigRepository(PgSqlConnectionFactory connectionFactory, ILogger<SystemConfigRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

    public virtual async Task<DbSystemConfig?> GetAsync(string configKey)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.QuerySingleOrDefaultAsync<DbSystemConfig>(
                """
                SELECT config_key AS ConfigKey,
                       COALESCE(config_value, '') AS ConfigValue,
                       updated_by AS UpdatedBy,
                       updated_at AS UpdatedAt
                FROM sys_config
                WHERE config_key = @ConfigKey
                """,
                new { ConfigKey = configKey });
        }
        catch (PostgresException ex) when (IsUndefinedTable(ex))
        {
            _logger?.LogDebug(ex, "系统配置表不存在，运行时配置读取回退启动配置。configKey={ConfigKey}", configKey);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "查询系统配置失败。configKey={ConfigKey}", configKey);
            return null;
        }
    }

    public virtual async Task<bool> SetAsync(string configKey, string configValue, string? updatedBy)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO sys_config(config_key, config_value, updated_by, updated_at)
                VALUES(@ConfigKey, @ConfigValue, @UpdatedBy, NOW())
                ON CONFLICT (config_key) DO UPDATE
                SET config_value = EXCLUDED.config_value,
                    updated_by = EXCLUDED.updated_by,
                    updated_at = EXCLUDED.updated_at
                """,
                new
                {
                    ConfigKey = configKey,
                    ConfigValue = configValue,
                    UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim()
                });
            return true;
        }
        catch (PostgresException ex) when (IsUndefinedTable(ex))
        {
            _logger?.LogError(ex, "系统配置表不存在，无法保存运行时配置。请先执行数据库迁移。configKey={ConfigKey}", configKey);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存系统配置失败。configKey={ConfigKey}", configKey);
            return false;
        }
    }

    private static bool IsUndefinedTable(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UndefinedTable;
}
