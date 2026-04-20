using Aura.Api.Internal;

namespace Aura.Api.Middleware;

internal sealed class PasswordChangeEnforcementMiddleware
{
    private static readonly PathString[] AllowedPaths =
    [
        new PathString("/api/auth/me"),
        new PathString("/api/auth/logout"),
        new PathString("/api/auth/change-password"),
        new PathString("/api/health"),
        new PathString("/api/health/live")
    ];

    private readonly RequestDelegate _next;

    public PasswordChangeEnforcementMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var mustChangePassword = string.Equals(
            user.FindFirst(AuraHelpers.MustChangePasswordClaimType)?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!mustChangePassword)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;
        if (AllowedPaths.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            code = 40321,
            msg = "当前账号需要先修改密码后才能继续使用"
        });
    }
}
