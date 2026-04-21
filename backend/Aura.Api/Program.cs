/* 文件：后端启动入口（Program.cs） | File: Backend entrypoint */
using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Extensions;
using Aura.Api.Internal;
using Aura.Api.Middleware;
using Aura.Api.Serialization;
using Aura.Api.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Prometheus;

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

builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole(options => options.FormatterName = "pure");
builder.Logging.AddConsoleFormatter<PureConsoleFormatter, ConsoleFormatterOptions>();

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new System.Globalization.CultureInfo("zh-CN");
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo("zh-CN");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    foreach (var converter in AuraJsonSerializerOptions.Default.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

builder.Services.AddOpenApi();
builder.Services.AddAuraServices(builder.Configuration, builder.Environment, isDev);
builder.Services.AddAuraOpenTelemetry(builder.Configuration, builder.Environment);

var app = builder.Build();

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

var projectRoot = ProjectPaths.ResolveProjectRoot(app.Environment);
var storageRoot = ProjectPaths.ResolveStorageRoot(app.Environment);
var frontendRootConfig = app.Configuration["Paths:FrontendRoot"]?.Trim();
var frontendRoot = string.IsNullOrWhiteSpace(frontendRootConfig)
    ? Path.Combine(projectRoot, "frontend")
    : Path.GetFullPath(frontendRootConfig);

var cspPolicy = builder.Configuration["Security:CspPolicy"]
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
    app.UseMiddleware<FrontendRoutingMiddleware>(frontendRoot);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(frontendRoot),
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

app.UseAuthentication();
app.UseMiddleware<PasswordChangeEnforcementMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuraEndpoints(builder.Configuration, isDev);

if (exposePrometheus)
{
    app.MapMetrics();
}

var dailyJudgeState = app.Services.GetRequiredService<DailyJudgeScheduleState>();
var cache = app.Services.GetRequiredService<RedisCacheService>();

dailyJudgeState.RunDailyAsync = async today =>
{
    using var scope = app.Services.CreateScope();
    var judgeService = scope.ServiceProvider.GetRequiredService<JudgeService>();
    var auditRepository = scope.ServiceProvider.GetRequiredService<AuditRepository>();

    const string lockKey = "aura:lock:daily-judges";
    string? lockToken = await cache.TryAcquireLockAsync(lockKey, TimeSpan.FromMinutes(60));
    if (lockToken is null && cache.Enabled)
    {
        return;
    }

    try
    {
        await judgeService.RunHomeAsync(today);
        await judgeService.RunGroupRentAndStayAsync(today, 2, 120);
        await judgeService.RunNightAbsenceAsync(today, 23);
        await auditRepository.InsertOperationAsync("\u7cfb\u7edf\u4efb\u52a1", "\u5f52\u5bdd\u5b9a\u65f6\u4efb\u52a1", $"\u65e5\u671f={today:yyyy-MM-dd}");
    }
    finally
    {
        if (lockToken is not null)
        {
            await cache.ReleaseLockAsync(lockKey, lockToken);
        }
    }
};

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Aura API started successfully.");
    logger.LogInformation("Listening on: {Urls}", string.Join(", ", app.Urls));
    logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);
    logger.LogInformation(
        "Observability: Prometheus={Prometheus}; Tracing={Tracing}",
        exposePrometheus ? "enabled" : "disabled",
        tracingConfigured ? "enabled" : "disabled");
    if (tracingRequested && !tracingConfigured)
    {
        logger.LogWarning("Ops:Telemetry:EnableTracing is enabled, but no OTLP endpoint is configured. Tracing stays disabled.");
    }

    logger.LogInformation("Press Ctrl+C to stop the service.");
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Shutdown signal received. Stopping service...");
});

app.Run();

internal sealed class PureConsoleFormatter : ConsoleFormatter
{
    public PureConsoleFormatter() : base("pure")
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        string? correlationId = null;
        scopeProvider?.ForEachScope<object?>((scope, _) =>
        {
            if (correlationId is not null)
            {
                return;
            }

            if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == CorrelationIdMiddleware.ScopeKey)
                    {
                        correlationId = pair.Value?.ToString();
                        return;
                    }
                }
            }
        }, null);

        var correlationPrefix = string.IsNullOrEmpty(correlationId) ? "" : $"[{correlationId}] ";
        var levelPrefix = logEntry.LogLevel switch
        {
            LogLevel.Warning => "[WARN] ",
            LogLevel.Error => "[ERROR] ",
            LogLevel.Critical => "[FATAL] ",
            LogLevel.Debug => "[DEBUG] ",
            LogLevel.Trace => "[TRACE] ",
            _ => ""
        };

        textWriter.WriteLine($"{correlationPrefix}{levelPrefix}{message}");
        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }
}
