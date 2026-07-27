using System.Net;
using Aura.Api.Internal;
using Aura.Api.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Prometheus;

namespace Aura.Api.Extensions;

public static class AuraApplicationExtensions
{
    public static WebApplication UseAuraPipeline(this WebApplication app, IConfiguration configuration, bool isDev, bool exposePrometheus)
    {
        app.UseAuraForwardedHeaders(configuration);
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

    private static void UseAuraForwardedHeaders(this WebApplication app, IConfiguration configuration)
    {
        var section = configuration.GetSection("Security:ForwardedHeaders");
        if (!section.GetValue<bool>("Enabled"))
        {
            return;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost,
            ForwardLimit = section.GetValue<int?>("ForwardLimit") ?? 2
        };

        var allowUnknownProxies = section.GetValue<bool>("AllowUnknownProxies");
        var knownProxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var knownNetworks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        if (allowUnknownProxies || knownProxies.Length > 0 || knownNetworks.Length > 0)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }

        if (!allowUnknownProxies)
        {
            foreach (var rawProxy in knownProxies)
            {
                if (IPAddress.TryParse(rawProxy, out var proxy))
                {
                    options.KnownProxies.Add(proxy);
                }
            }

            foreach (var rawNetwork in knownNetworks)
            {
                if (TryParseIpNetwork(rawNetwork, out var network))
                {
                    options.KnownIPNetworks.Add(network);
                }
            }
        }

        app.UseForwardedHeaders(options);
    }

    private static bool TryParseIpNetwork(string? value, out System.Net.IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var prefix)
            || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maxPrefixLength = prefix.GetAddressBytes().Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            return false;
        }

        network = new System.Net.IPNetwork(prefix, prefixLength);
        return true;
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

        // Private runtime artifacts are served by AuraEndpointsStorage after
        // authentication and domain-specific authorization checks.
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



