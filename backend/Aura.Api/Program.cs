using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

/* 文件：后端启动入口（Program.cs） | File: Backend Startup Entry */
using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Extensions;
using Aura.Api.Internal;
using Aura.Api.Middleware;
using Aura.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var isDev = builder.Environment.IsDevelopment();

// 配置纯净日志格式
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = "pure");
builder.Logging.AddConsoleFormatter<PureConsoleFormatter, ConsoleFormatterOptions>();

// 设置全中文文化区域
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new System.Globalization.CultureInfo("zh-CN");
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo("zh-CN");

// 配置 JSON 选项
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddOpenApi();

// 使用扩展方法注册 Aura 相关服务
builder.Services.AddAuraServices(builder.Configuration, builder.Environment, isDev);
builder.Services.AddAuraOpenTelemetry(builder.Configuration, builder.Environment);

var app = builder.Build();

// 关联 ID 与全局异常（须尽早，以便异常响应携带 traceId）
app.UseMiddleware<CorrelationIdMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseAuraGlobalExceptionHandler();
}

// Prometheus HTTP 指标（与 MapMetrics 配套；可由 Ops:Metrics:ExposePrometheus 关闭）
app.UseRouting();
app.UseHttpMetrics();

// 计算路径（统一仓库根 storage，见 Internal/ProjectPaths）
var projectRoot = ProjectPaths.ResolveProjectRoot(app.Environment);
var storageRoot = ProjectPaths.ResolveStorageRoot(app.Environment);
var frontendRootCfg = app.Configuration["Paths:FrontendRoot"]?.Trim();
var frontendRoot = string.IsNullOrWhiteSpace(frontendRootCfg)
    ? Path.Combine(projectRoot, "frontend")
    : Path.GetFullPath(frontendRootCfg);

var cspPolicy = builder.Configuration["Security:CspPolicy"]
    ?? "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss:;";

// 使用自定义中间件设置安全头
app.UseMiddleware<SecurityHeadersMiddleware>(cspPolicy);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseHttpsRedirection();
    // 开发库初始化访问 PostgreSQL，勿阻塞 Kestrel 监听；失败由 DevInitializer 记录日志
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

// 静态资源与前端路由
if (Directory.Exists(frontendRoot))
{
    app.UseMiddleware<FrontendRoutingMiddleware>(frontendRoot);
    
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

app.UseAuthentication();
app.UseAuthorization();

// 使用扩展方法映射路由
app.MapAuraEndpoints(builder.Configuration, isDev);

if (app.Configuration.GetValue("Ops:Metrics:ExposePrometheus", true))
{
    // 暴露 Prometheus 抓取端点（生产环境请通过网络策略或反向代理限制访问范围）
    app.MapMetrics();
}

// 注册定时研判逻辑
var dailyJudgeState = app.Services.GetRequiredService<DailyJudgeScheduleState>();
var cache = app.Services.GetRequiredService<RedisCacheService>();

dailyJudgeState.RunDailyAsync = async (today) =>
{
    using var scope = app.Services.CreateScope();
    var judgeService = scope.ServiceProvider.GetRequiredService<JudgeService>();
    var db = scope.ServiceProvider.GetRequiredService<PgSqlStore>();
    
    const string lockKey = "aura:lock:daily-judges";
    string? lockToken = await cache.TryAcquireLockAsync(lockKey, TimeSpan.FromMinutes(60));
    if (lockToken is null && cache.Enabled) return;
    try
    {
        await judgeService.RunHomeAsync(today);
        await judgeService.RunGroupRentAndStayAsync(today, 2, 120);
        await judgeService.RunNightAbsenceAsync(today, 23);
        await db.InsertOperationAsync("系统任务", "归寝定时任务", $"日期={today:yyyy-MM-dd}");
    }
    finally
    {
        if (lockToken is not null) await cache.ReleaseLockAsync(lockKey, lockToken);
    }
};

// 自定义中文生命周期日志
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var urls = string.Join(", ", app.Urls);
    logger.LogInformation("寓瞳中枢服务已成功启动。");
    logger.LogInformation("正在监听：{Urls}", urls);
    logger.LogInformation("运行环境：{Environment}", app.Environment.EnvironmentName);
    logger.LogInformation("按 Ctrl+C 键停止服务。");
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("正在接收停止信号，准备关闭服务...");
});

app.Run();

// 纯净日志格式化器
internal sealed class PureConsoleFormatter : ConsoleFormatter
{
    public PureConsoleFormatter() : base("pure") { }
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message)) return;

        string? correlation = null;
        scopeProvider?.ForEachScope<object?>((scope, _) =>
        {
            if (correlation is not null) return;
            if (scope is IEnumerable<KeyValuePair<string, object>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (kv.Key == CorrelationIdMiddleware.ScopeKey)
                    {
                        correlation = kv.Value?.ToString();
                        return;
                    }
                }
            }
        }, null);
        var correlationPrefix = string.IsNullOrEmpty(correlation) ? "" : $"[{correlation}] ";

        var prefix = logEntry.LogLevel switch
        {
            LogLevel.Information => "",
            LogLevel.Warning => "[警告] ",
            LogLevel.Error => "[错误] ",
            LogLevel.Critical => "[致命] ",
            LogLevel.Debug => "[调试] ",
            LogLevel.Trace => "[追踪] ",
            _ => ""
        };

        textWriter.WriteLine($"{correlationPrefix}{prefix}{message}");
        if (logEntry.Exception != null)
        {
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }
}
