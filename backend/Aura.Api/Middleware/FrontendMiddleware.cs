/* 文件：前端路由与安全响应头中间件（FrontendMiddleware.cs） | File: Frontend routing and security headers middleware */
using Aura.Api.Internal;
using Microsoft.Extensions.FileProviders;

namespace Aura.Api.Middleware;

public static class FrontendMiddleware
{
    public static IApplicationBuilder UseAuraFrontend(this IApplicationBuilder app, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var cspPolicy = configuration["Security:CspPolicy"]
            ?? "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:;";

        var projectRoot = ProjectPaths.ResolveProjectRoot(environment);
        var storageRoot = ProjectPaths.ResolveStorageRoot(environment);
        var frontendRootCfg = configuration["Paths:FrontendRoot"]?.Trim();
        var frontendRoot = string.IsNullOrWhiteSpace(frontendRootCfg)
            ? Path.Combine(projectRoot, "frontend")
            : Path.GetFullPath(frontendRootCfg);

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] = cspPolicy;
            await next();
        });

        if (Directory.Exists(frontendRoot))
        {
            app.Use(async (context, next) =>
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
                                context.Response.Redirect($"/login/?returnUrl={returnUrl}", permanent: false);
                                return;
                            }
                        }

                        var seg = path.Trim('/');
                        if (!string.IsNullOrWhiteSpace(seg))
                        {
                            var htmlFile = Path.Combine(frontendRoot, seg, $"{seg}.html");
                            if (File.Exists(htmlFile))
                            {
                                if (!path.EndsWith("/", StringComparison.Ordinal))
                                {
                                    context.Response.Redirect($"{path}/", permanent: false);
                                    return;
                                }
                                context.Request.Path = $"/{seg}/{seg}.html";
                            }
                        }
                    }
                }
                await next();
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(frontendRoot),
                RequestPath = ""
            });
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(storageRoot),
            RequestPath = "/storage"
        });

        return app;
    }
}

