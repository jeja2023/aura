using Aura.Api.Middleware;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Aura.Api.Logging;

internal sealed class PureConsoleFormatter : ConsoleFormatter
{
    public PureConsoleFormatter() : base("pure")
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string? correlationId = null;
        scopeProvider?.ForEachScope<object?>((scope, _) =>
        {
            if (correlationId is not null)
            {
                return;
            }

            if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == CorrelationIdMiddleware.ScopeKey)
                    {
                        correlationId = pair.Value?.ToString();
                        return;
                    }
                }
            }
        }, null);

        var timestampPrefix = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ";
        var correlationPrefix = string.IsNullOrEmpty(correlationId) ? "" : $"[{correlationId}] ";
        var levelPrefix = logEntry.LogLevel switch
        {
            LogLevel.Warning => "[WARN] ",
            LogLevel.Error => "[ERROR] ",
            LogLevel.Critical => "[FATAL] ",
            LogLevel.Debug => "[DEBUG] ",
            LogLevel.Trace => "[TRACE] ",
            _ => ""
        };

        textWriter.WriteLine($"{timestampPrefix}{correlationPrefix}{levelPrefix}{message}");
        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }
}
