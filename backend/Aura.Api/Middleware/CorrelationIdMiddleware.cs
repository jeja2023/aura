/* 文件：请求关联 ID 中间件 | File: Correlation ID middleware */
using Microsoft.Extensions.Primitives;

namespace Aura.Api.Middleware;

/// <summary>
/// 自请求头 <c>X-Correlation-Id</c> 透传或生成关联 ID，写入日志作用域与响应头，便于链路排查。
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "Aura.CorrelationId";
    public const string ScopeKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers.TryGetValue(HeaderName, out StringValues fromHeader) && !StringValues.IsNullOrEmpty(fromHeader)
            ? fromHeader.ToString().Trim()
            : Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.Response.Headers.Append(HeaderName, id);

        using (_logger.BeginScope(new Dictionary<string, object> { [ScopeKey] = id }))
        {
            await _next(context);
        }
    }
}
