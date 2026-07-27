/* 文件：仓储通用辅助（PgSqlRepositoryHelpers.cs） */
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Diagnostics;

namespace Aura.Api.Data;

internal sealed record DbPagedResult<T>(List<T> Rows, int Total, bool Succeeded);

/// <summary>
/// 统一各仓储中重复的数据库调用样板：
///   await using var conn = factory.CreateConnection();
///   var ret = await operation(conn);
/// 失败时记录上下文；数据库已配置时抛出统一的不可用异常，仅无数据库开发模式返回兜底值。
/// </summary>
internal static class PgSqlRepositoryHelpers
{
    private const long SlowOperationWarningMs = 500;

    public static void LogIfSlow(ILogger? logger, string operationLabel, long elapsedMs, object? logContext = null)
    {
        if (elapsedMs < SlowOperationWarningMs)
        {
            return;
        }

        if (logContext is null)
        {
            logger?.LogWarning("{Operation} 耗时较长。elapsedMs={ElapsedMs}", operationLabel, elapsedMs);
        }
        else
        {
            logger?.LogWarning("{Operation} 耗时较长。elapsedMs={ElapsedMs}, context={Context}", operationLabel, elapsedMs, logContext);
        }
    }

    /// <summary>查询型：仅在未配置数据库时返回 <paramref name="fallback"/> 指定的兜底值。</summary>
    public static async Task<T> ExecuteAsync<T>(
        PgSqlConnectionFactory factory,
        ILogger? logger,
        string operationLabel,
        Func<NpgsqlConnection, Task<T>> operation,
        T fallback,
        LogLevel logLevel = LogLevel.Error,
        object? logContext = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = factory.CreateConnection();
            var result = await operation(conn).ConfigureAwait(false);
            LogIfSlow(logger, operationLabel, sw.ElapsedMilliseconds, logContext);
            return result;
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
            ThrowIfConfigured(factory, ex, operationLabel);
            return fallback;
        }
    }

    /// <summary>无返回值写入：已配置数据库时失败抛出统一异常。</summary>
    public static async Task<bool> ExecuteVoidAsync(
        PgSqlConnectionFactory factory,
        ILogger? logger,
        string operationLabel,
        Func<NpgsqlConnection, Task> operation,
        LogLevel logLevel = LogLevel.Warning,
        object? logContext = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = factory.CreateConnection();
            await operation(conn).ConfigureAwait(false);
            LogIfSlow(logger, operationLabel, sw.ElapsedMilliseconds, logContext);
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
            ThrowIfConfigured(factory, ex, operationLabel);
            return false;
        }
    }

    public static void ThrowIfConfigured(PgSqlConnectionFactory factory, Exception exception, string operation)
    {
        if (factory.IsConfigured)
        {
            throw new DataAccessUnavailableException(operation, exception);
        }
    }
}
