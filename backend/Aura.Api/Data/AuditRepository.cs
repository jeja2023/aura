/* 文件：审计日志仓储 | File: Audit repository */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

internal sealed class AuditRepository
{
    private readonly PgSqlConnectionFactory _connectionFactory;
    private readonly ILogger<AuditRepository>? _logger;

    public AuditRepository(PgSqlConnectionFactory connectionFactory, ILogger<AuditRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private NpgsqlConnection CreateConnection() => _connectionFactory.CreateConnection();

    public async Task<long?> InsertOperationAsync(string operatorName, string action, string detail)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO log_operation(operator_name, action_type, action_detail, created_at)
                VALUES(@OperatorName, @Action, @Detail, NOW())
                RETURNING op_id
                """,
                new { OperatorName = operatorName, Action = action, Detail = detail });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入操作日志失败。operator={OperatorName}, action={Action}", operatorName, action);
            return null;
        }
    }

    public async Task<(List<DbOperation> Rows, int Total)> GetOperationsAsync(string? keyword, int page, int pageSize)
    {
        try
        {
            await using var conn = CreateConnection();
            var filter = string.IsNullOrWhiteSpace(keyword) ? "" : " WHERE operator_name ILIKE @kw OR action_type ILIKE @kw OR action_detail ILIKE @kw ";
            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM log_operation {filter}",
                new { kw = $"%{keyword}%" });
            var rows = await conn.QueryAsync<DbOperation>(
                $"""
                SELECT op_id AS OperationId, operator_name AS OperatorName, action_type AS Action,
                       action_detail AS Detail, created_at AS CreatedAt
                FROM log_operation
                {filter}
                ORDER BY op_id DESC
                LIMIT @pageSize OFFSET @offset
                """,
                new { kw = $"%{keyword}%", offset = (page - 1) * pageSize, pageSize });
            return (rows.ToList(), total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询操作日志失败。keyword={Keyword}, page={Page}, pageSize={PageSize}", keyword, page, pageSize);
            return ([], 0);
        }
    }

    public async Task<long?> InsertSystemLogAsync(string level, string source, string message)
    {
        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO log_system(level, source, message, created_at)
                VALUES(@Level, @Source, @Message, NOW())
                RETURNING system_log_id
                """,
                new { Level = level, Source = source, Message = message });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库写入系统日志失败。level={Level}, source={Source}", level, source);
            return null;
        }
    }

    public async Task<(List<DbSystemLog> Rows, int Total)> GetSystemLogsAsync(string? keyword, int page, int pageSize)
    {
        try
        {
            await using var conn = CreateConnection();
            var filter = string.IsNullOrWhiteSpace(keyword) ? "" : " WHERE level ILIKE @kw OR source ILIKE @kw OR message ILIKE @kw ";
            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM log_system {filter}",
                new { kw = $"%{keyword}%" });
            var rows = await conn.QueryAsync<DbSystemLog>(
                $"""
                SELECT system_log_id AS SystemLogId, level AS Level, source AS Source,
                       message AS Message, created_at AS CreatedAt
                FROM log_system
                {filter}
                ORDER BY system_log_id DESC
                LIMIT @pageSize OFFSET @offset
                """,
                new { kw = $"%{keyword}%", offset = (page - 1) * pageSize, pageSize });
            return (rows.ToList(), total);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询系统日志失败。keyword={Keyword}, page={Page}, pageSize={PageSize}", keyword, page, pageSize);
            return ([], 0);
        }
    }
}
