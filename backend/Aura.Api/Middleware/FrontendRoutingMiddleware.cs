using Microsoft.Extensions.FileProviders;

namespace Aura.Api.Middleware;

public sealed class FrontendRoutingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _frontendRoot;

    public FrontendRoutingMiddleware(RequestDelegate next, string frontendRoot)
    {
        _next = next;
        _frontendRoot = frontendRoot;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            var path = context.Request.Path.Value ?? "/";
            var isReserved = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("/storage", StringComparison.OrdinalIgnoreCase);

            if (!isReserved && !Path.HasExtension(path))
            {
                var isLoginPath = path.Equals("/login", StringComparison.OrdinalIgnoreCase) || path.Equals("/login/", StringComparison.OrdinalIgnoreCase);
                if (!isLoginPath)
                {
                    var token = context.Request.Cookies["aura_token"];
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        var returnUrl = Uri.EscapeDataString(path + context.Request.QueryString);
                        context.Response.Redirect($"/login/?returnUrl={returnUrl}", false);
                        return;
                    }
                }

                var seg = path.Trim('/');
                if (!string.IsNullOrWhiteSpace(seg))
                {
                    var htmlFile = Path.Combine(_frontendRoot, seg, $"{seg}.html");
                    if (File.Exists(htmlFile))
                    {
                        if (!path.EndsWith("/", StringComparison.Ordinal))
                        {
                            context.Response.Redirect($"{path}/", false);
                            return;
                        }
                        context.Request.Path = $"/{seg}/{seg}.html";
                    }
                }
            }
        }
        await _next(context);
    }
}
