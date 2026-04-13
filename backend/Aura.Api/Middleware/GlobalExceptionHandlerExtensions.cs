/* 文件：全局异常处理扩展 | File: Global exception handler extensions */
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Middleware;

internal static class GlobalExceptionHandlerExtensions
{
    /// <summary>
    /// 非开发环境统一返回 JSON，包含 traceId（优先关联 ID），不向客户端暴露异常堆栈。
    /// </summary>
    public static void UseAuraGlobalExceptionHandler(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var feature = context.Features.Get<IExceptionHandlerFeature>();
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("全局异常");
                if (feature?.Error is not null)
                {
                    logger.LogError(feature.Error, "未处理的请求异常。");
                }

                var traceId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) && cid is not null
                    ? cid.ToString()
                    : context.TraceIdentifier;

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new
                {
                    code = 50000,
                    msg = "服务内部错误，请稍后重试或联系管理员。",
                    traceId
                });
            });
        });
    }
}
