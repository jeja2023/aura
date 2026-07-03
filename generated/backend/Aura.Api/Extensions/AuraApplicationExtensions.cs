using Aura.Api.Internal;
using Aura.Api.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Prometheus;

namespace Aura.Api.Extensions;

public static class AuraApplicationExtensions
{
    public static WebApplication UseAuraPipeline(this WebApplication app, IConfiguration configuration, bool isDev, bool exposePrometheus)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseAuraGlobalExceptionHandler();
        }

        app.UseRouting();
        if (exposePrometheus)
        {
            app.UseHttpMetrics();
        }

        app.UseAuraStaticContent(configuration);

        app.UseAuthentication();
        app.UseMiddleware<PasswordChangeEnforcementMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapAuraEndpoints(configuration, isDev);

        if (exposePrometheus)
        {
            app.MapMetrics();
        }

        return app;
    }

    private static void UseAuraStaticContent(this WebApplication app, IConfiguration configuration)
    {
        var projectRoot = ProjectPaths.ResolveProjectRoot(app.Environment);
        var storageRoot = ProjectPaths.ResolveStorageRoot(app.Environment);
        var frontendRootConfig = configuration["Paths:FrontendRoot"]?.Trim();
        var frontendRoot = string.IsNullOrWhiteSpace(frontendRootConfig)
            ? Path.Combine(projectRoot, "frontend")
            : Path.GetFullPath(frontendRootConfig);
        var frontendOverrideRoot = Path.Combine(projectRoot, "frontend-overrides");

        var cspPolicy = configuration["Security:CspPolicy"]
            ?? "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:;";

        app.UseMiddleware<SecurityHeadersMiddleware>(cspPolicy);

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseHttpsRedirection();
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    await DevInitializer.InitializeDevDataAsync(app);
                });
            });
        }
        else
        {
            app.UseHsts();
        }

        if (Directory.Exists(frontendRoot))
        {
            var frontendProvider = CreateFrontendProvider(frontendRoot, frontendOverrideRoot);
            app.UseMiddleware<FrontendRoutingMiddleware>(frontendProvider);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = frontendProvider,
                RequestPath = ""
            });
        }

        if (!Directory.Exists(storageRoot))
        {
            Directory.CreateDirectory(storageRoot);
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(storageRoot),
            RequestPath = "/storage"
        });
    }

    private static IFileProvider CreateFrontendProvider(string frontendRoot, string overlayRoot)
    {
        var baseProvider = new PhysicalFileProvider(frontendRoot);
        if (!Directory.Exists(overlayRoot))
        {
            return baseProvider;
        }

        return new CompositeFileProvider(new PhysicalFileProvider(overlayRoot), baseProvider);
    }
}


