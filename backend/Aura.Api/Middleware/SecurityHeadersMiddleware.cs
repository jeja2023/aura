namespace Aura.Api.Middleware;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _cspPolicy;

    public SecurityHeadersMiddleware(RequestDelegate next, string cspPolicy)
    {
        _next = next;
        _cspPolicy = cspPolicy;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["Content-Security-Policy"] = _cspPolicy;
        return _next(context);
    }
}
