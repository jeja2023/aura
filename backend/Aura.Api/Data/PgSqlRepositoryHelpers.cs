/* 文件：仓储通用辅助（PgSqlRepositoryHelpers.cs） | File: PgSql repository helpers */
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Api.Data;

/// <summary>
/// 统一各 Repository 中重复的「try { 数据库调用 } catch { 记日志返回 fallback }」样板：
///   await using var conn = factory.CreateConnection();
///   var ret = await operation(conn);
/// 失败时根据签名返回 fallback（null / [] / false 等），并按 <paramref name="logLevel"/> 记录上下文。
/// </summary>
internal static class PgSqlRepositoryHelpers
{
    /// <summary>查询型：失败时返回 <paramref name="fallback"/>。</summary>
    public static async Task<T> ExecuteAsync<T>(
        PgSqlConnectionFactory factory,
        ILogger? logger,
        string operationLabel,
        Func<NpgsqlConnection, Task<T>> operation,
        T fallback,
        LogLevel logLevel = LogLevel.Error,
        object? logContext = null)
    {
        try
        {
            await using var conn = factory.CreateConnection();
            return await operation(conn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logContext is null)
            {
                logger?.Log(logLevel, ex, "{Operation} 失败。", operationLabel);
            }
            else
            {
                logger?.Log(logLevel, ex, "{Operation} 失败。context={Context}", operationLabel, logContext);
            }
            return fallback;
        }
    }

    /// <summary>无返回值（fire-and-forget 写入），失败仅记日志，不抛出。</summary>
    public static async Task<bool> ExecuteVoidAsync(
        PgSqlConnectionFactory factory,
        ILogger? logger,
        string operationLabel,
        Func<NpgsqlConnection, Task> operation,
        LogLevel logLevel = LogLevel.Warning,
        object? logContext = null)
    {
        try
        {
            await using var conn = factory.CreateConnection();
            await operation(conn).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (logContext is null)
            {
                logger?.Log(logLevel, ex, "{Operation} 失败。", operationLabel);
            }
            else
            {
                logger?.Log(logLevel, ex, "{Operation} 失败。context={Context}", operationLabel, logContext);
            }
            return false;
        }
    }
}
