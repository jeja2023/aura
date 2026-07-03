/* 文件：后端启动入口（Program.cs） | File: Backend entrypoint */
using Aura.Api.Extensions;
using Aura.Api.Internal;

var builder = WebApplication.CreateBuilder(args);
var isDev = builder.Environment.IsDevelopment();
var exposePrometheus = builder.Configuration.GetValue<bool?>("Ops:Metrics:ExposePrometheus") ?? isDev;
var tracingRequested = builder.Configuration.GetValue<bool?>("Ops:Telemetry:EnableTracing") ?? false;
var tracingEndpoint = builder.Configuration["Ops:Telemetry:OtlpEndpoint"]?.Trim();
if (string.IsNullOrWhiteSpace(tracingEndpoint))
{
    tracingEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")?.Trim();
}

var tracingConfigured = tracingRequested && !string.IsNullOrWhiteSpace(tracingEndpoint);

builder.ConfigureAuraHost();
builder.Services.AddAuraServices(builder.Configuration, builder.Environment, isDev);
builder.Services.AddAuraOpenTelemetry(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseAuraPipeline(builder.Configuration, isDev, exposePrometheus);

await AdminBootstrapper.InitializeAsync(app);

app.ConfigureAuraLifecycle(exposePrometheus, tracingRequested, tracingConfigured);

app.Run();
