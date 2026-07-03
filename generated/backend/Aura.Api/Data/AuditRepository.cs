/* 文件：审计日志仓储 */
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Diagnostics;

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

    private static DateTime ToLocalTimestamp(DateTimeOffset value) => value.LocalDateTime;

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

    public async Task<DbPagedResult<DbOperation>> GetOperationsAsync(
        string? keyword,
        int page,
        int pageSize,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 20000);

        try
        {
            var sw = Stopwatch.StartNew();
            await using var conn = CreateConnection();
            var trimmedKeyword = keyword?.Trim();
            var (whereSql, args) = BuildOperationFilter(trimmedKeyword, from, to);

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM log_operation {whereSql}",
                args);
            var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            var actualPage = Math.Min(page, maxPage);
            args.Add("offset", (actualPage - 1) * pageSize);
            args.Add("pageSize", pageSize);

            var rows = await conn.QueryAsync<DbOperation>(
                $"""
                SELECT op_id AS OperationId, operator_name AS OperatorName, action_type AS Action,
                       action_detail AS Detail, created_at AS CreatedAt
                FROM log_operation
                {whereSql}
                ORDER BY created_at DESC, op_id DESC
                LIMIT @pageSize OFFSET @offset
                """,
                args);
            PgSqlRepositoryHelpers.LogIfSlow(_logger, "数据库查询操作日志", sw.ElapsedMilliseconds, new { keyword = trimmedKeyword, from, to, page, pageSize });
            return new DbPagedResult<DbOperation>(rows.ToList(), total, true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询操作日志失败。keyword={Keyword}, from={From}, to={To}, page={Page}, pageSize={PageSize}", keyword, from, to, page, pageSize);
            return new DbPagedResult<DbOperation>([], 0, false);
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

    public async Task<DbPagedResult<DbSystemLog>> GetSystemLogsAsync(
        string? keyword,
        int page,
        int pageSize,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 20000);

        try
        {
            var sw = Stopwatch.StartNew();
            await using var conn = CreateConnection();
            var trimmedKeyword = keyword?.Trim();
            var (whereSql, args) = BuildSystemFilter(trimmedKeyword, from, to);

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(1) FROM log_system {whereSql}",
                args);
            var maxPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            var actualPage = Math.Min(page, maxPage);
            args.Add("offset", (actualPage - 1) * pageSize);
            args.Add("pageSize", pageSize);

            var rows = await conn.QueryAsync<DbSystemLog>(
                $"""
                SELECT system_log_id AS SystemLogId, level AS Level, source AS Source,
                       message AS Message, created_at AS CreatedAt
                FROM log_system
                {whereSql}
                ORDER BY created_at DESC, system_log_id DESC
                LIMIT @pageSize OFFSET @offset
                """,
                args);
            PgSqlRepositoryHelpers.LogIfSlow(_logger, "数据库查询系统日志", sw.ElapsedMilliseconds, new { keyword = trimmedKeyword, from, to, page, pageSize });
            return new DbPagedResult<DbSystemLog>(rows.ToList(), total, true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库查询系统日志失败。keyword={Keyword}, from={From}, to={To}, page={Page}, pageSize={PageSize}", keyword, from, to, page, pageSize);
            return new DbPagedResult<DbSystemLog>([], 0, false);
        }
    }

    private static (string WhereSql, DynamicParameters Args) BuildOperationFilter(string? keyword, DateTimeOffset? from, DateTimeOffset? to)
    {
        var where = new List<string>();
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(operator_name ILIKE @kw OR action_type ILIKE @kw OR action_detail ILIKE @kw)");
            args.Add("kw", $"%{keyword}%");
        }

        AddTimeRange(where, args, from, to);
        return (where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where), args);
    }

    private static (string WhereSql, DynamicParameters Args) BuildSystemFilter(string? keyword, DateTimeOffset? from, DateTimeOffset? to)
    {
        var where = new List<string>();
        var args = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(level ILIKE @kw OR source ILIKE @kw OR message ILIKE @kw)");
            args.Add("kw", $"%{keyword}%");
        }

        AddTimeRange(where, args, from, to);
        return (where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where), args);
    }

    private static void AddTimeRange(List<string> where, DynamicParameters args, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from.HasValue)
        {
            where.Add("created_at >= @From");
            args.Add("From", ToLocalTimestamp(from.Value));
        }

        if (to.HasValue)
        {
            where.Add("created_at <= @To");
            args.Add("To", ToLocalTimestamp(to.Value));
        }
    }
}


