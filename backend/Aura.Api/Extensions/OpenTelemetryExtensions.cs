using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aura.Api.Extensions;

/// <summary>
/// OpenTelemetry 链路追踪注册（按需启用，避免无采集端时产生无效导出）。
/// </summary>
public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddAuraOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var enable = configuration.GetValue("Ops:Telemetry:EnableTracing", false);
        var endpoint = configuration["Ops:Telemetry:OtlpEndpoint"]?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")?.Trim();
        }

        if (!enable || string.IsNullOrWhiteSpace(endpoint))
        {
            return services;
        }

        var serviceName = configuration["Ops:Telemetry:ServiceName"]?.Trim();
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")?.Trim();
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = environment.ApplicationName?.Trim();
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = "Aura.Api";
        }

        var version = typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var protocolRaw = configuration["Ops:Telemetry:OtlpProtocol"]?.Trim();

        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(
                        ResourceBuilder.CreateDefault()
                            .AddService(serviceName, serviceVersion: version))
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.Filter = ctx =>
                        {
                            var path = ctx.Request.Path.Value ?? "";
                            return !string.Equals(path, "/metrics", StringComparison.OrdinalIgnoreCase);
                        };
                    })
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(endpoint);
                        if (string.Equals(protocolRaw, "HttpProtobuf", StringComparison.OrdinalIgnoreCase))
                        {
                            o.Protocol = OtlpExportProtocol.HttpProtobuf;
                        }
                    });
            });

        return services;
    }
}
