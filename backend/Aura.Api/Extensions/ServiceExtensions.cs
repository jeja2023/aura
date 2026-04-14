using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api;
using Aura.Api.Capture;
using Aura.Api.Clustering;
using Aura.Api.Data;
using Aura.Api.Export;
using Aura.Api.Ops;
using Aura.Api.Services;
using Aura.Api.Internal;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace Aura.Api.Extensions;

public static class ServiceExtensions
{
    private static string ResolveCaptureRetryFolder(IConfiguration configuration, IHostEnvironment env)
    {
        var projectRoot = ProjectPaths.ResolveProjectRoot(env);
        var storageRoot = ProjectPaths.ResolveStorageRoot(env);
        var retryRoot = configuration["Storage:CaptureRetryRoot"];
        if (string.IsNullOrWhiteSpace(retryRoot))
        {
            return Path.Combine(storageRoot, "captures", "retry");
        }

        return Path.IsPathRooted(retryRoot)
            ? Path.GetFullPath(retryRoot)
            : Path.GetFullPath(Path.Combine(projectRoot, retryRoot));
    }

    public static IServiceCollection AddAuraServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment hostEnvironment, bool isDev)
    {
        var jwtKey = configuration["Jwt:Key"];
        var jwtIssuer = configuration["Jwt:Issuer"] ?? "Aura.Api";
        var jwtAudience = configuration["Jwt:Audience"] ?? "Aura.Client";
        
        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            if (!isDev) throw new InvalidOperationException("JWT Key 未配置（生产环境必须配置）");
            jwtKey = "aura-dev-jwt-key-please-change";
        }

        var jwtExpireMinutes = int.TryParse(configuration["Jwt:ExpireMinutes"], out var jm) ? jm : 480;

        var pgsqlConn = configuration.GetConnectionString("PgSql") ?? "";
        var redisConn = configuration.GetConnectionString("Redis") ?? "";
        var alertWebhookUrl = configuration["Ops:Alert:WebhookUrl"];
        var alertNotifyFilePath = ProjectPaths.ResolvePathRelativeToProjectRoot(hostEnvironment, configuration["Ops:Alert:FilePath"]);

        var aiTotalTimeout = configuration.GetValue("HttpClients:Ai:TotalRequestTimeoutSeconds", 120);
        var aiAttemptTimeout = configuration.GetValue("HttpClients:Ai:AttemptTimeoutSeconds", 90);
        var aiMaxRetries = configuration.GetValue("HttpClients:Ai:MaxRetryAttempts", 2);
        var alertTotalTimeout = configuration.GetValue("HttpClients:AlertNotifier:TotalRequestTimeoutSeconds", 30);
        var alertAttemptTimeout = configuration.GetValue("HttpClients:AlertNotifier:AttemptTimeoutSeconds", 15);
        var alertMaxRetries = configuration.GetValue("HttpClients:AlertNotifier:MaxRetryAttempts", 2);

        services.AddSingleton<PgSqlStore>(sp =>
            new PgSqlStore(pgsqlConn, sp.GetRequiredService<ILogger<PgSqlStore>>()));
        
        services.AddSingleton<RedisCacheService>(sp =>
            new RedisCacheService(redisConn, sp.GetRequiredService<ILogger<RedisCacheService>>()));
        
        services.AddSingleton<RetryQueueService>(sp =>
            new RetryQueueService(redisConn, sp.GetRequiredService<ILogger<RetryQueueService>>()));
        
        services.AddHttpClient(AuraHttpClientNames.AlertNotifier)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(alertTotalTimeout);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(alertAttemptTimeout);
                options.Retry.MaxRetryAttempts = alertMaxRetries;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(60, alertAttemptTimeout * 2 + 10));
            });
        services.AddSingleton<IAlertNotifier>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(AuraHttpClientNames.AlertNotifier);
            return new AlertNotifier(
                client,
                sp.GetRequiredService<ILogger<AlertNotifier>>(),
                alertWebhookUrl,
                alertNotifyFilePath);
        });

        services.AddSingleton<DailyJudgeScheduleState>();
        services.AddHostedService<DailyJudgeHostedService>();
        services.AddSingleton<AppStore>();
        services.AddHttpClient(AuraHttpClientNames.AiService)
            .ConfigureHttpClient((sp, c) =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan;
                var aiKey = sp.GetRequiredService<IConfiguration>()["Ai:ApiKey"]?.Trim();
                if (!string.IsNullOrEmpty(aiKey))
                {
                    c.DefaultRequestHeaders.TryAddWithoutValidation("X-Aura-Ai-Key", aiKey);
                }
            })
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(aiTotalTimeout);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(aiAttemptTimeout);
                options.Retry.MaxRetryAttempts = aiMaxRetries;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(120, aiAttemptTimeout * 2 + 30));
            });
        services.AddSingleton<AiClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(AuraHttpClientNames.AiService);
            return new AiClient(client, configuration["Ai:BaseUrl"] ?? "http://127.0.0.1:8000", sp.GetRequiredService<ILogger<AiClient>>());
        });

        services.AddSingleton<FeatureClusteringService>();
        services.AddSingleton<TabularExportService>();
        
        // 业务逻辑服务建议使用 Scoped 声明周期以符合标准 Web API 模式
        services.AddScoped<IdentityAdminService>(sp => 
            new IdentityAdminService(
                sp.GetRequiredService<AppStore>(),
                sp.GetRequiredService<PgSqlStore>(),
                sp.GetRequiredService<RedisCacheService>(),
                sp.GetRequiredService<ILogger<IdentityAdminService>>(),
                jwtKey ?? "aura-dev-jwt-key-please-change",
                jwtIssuer,
                jwtAudience,
                jwtExpireMinutes));

        services.AddScoped<DeviceManagementService>();
        services.AddScoped<EventDispatchService>();
        services.AddScoped<ClusterApplicationService>();
        services.AddScoped<StatsApplicationService>();
        services.AddScoped<ExportApplicationService>(sp => new ExportApplicationService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<PgSqlStore>(),
            sp.GetRequiredService<TabularExportService>(),
            ProjectPaths.ResolveStorageRoot(hostEnvironment)));
        services.AddScoped<OutputApplicationService>();
        services.AddScoped<VectorApplicationService>(sp => new VectorApplicationService(
            sp.GetRequiredService<AiClient>(),
            configuration.GetValue("Limits:MaxImageBase64Chars", 5_000_000),
            configuration.GetValue("Limits:MaxMetadataJsonChars", 200_000)));
        services.AddScoped<SpaceCollisionService>();
        services.AddScoped<JudgeService>();
        services.AddScoped<MonitoringQueryService>();
        services.AddScoped<CaptureProcessingService>(sp => new CaptureProcessingService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<PgSqlStore>(),
            sp.GetRequiredService<RetryQueueService>(),
            sp.GetRequiredService<AiClient>(),
            sp.GetRequiredService<EventDispatchService>(),
            ProjectPaths.ResolveStorageRoot(hostEnvironment),
            ResolveCaptureRetryFolder(configuration, hostEnvironment),
            configuration.GetValue("CaptureRetry:PreferInlineBase64", false),
            configuration.GetValue("CaptureRetry:AllowInlineBase64Fallback", false),
            configuration.GetValue("Storage:SaveCaptureImageOnSuccess", true)));
        services.AddScoped<RetryProcessingService>();
        services.AddScoped<ResourceManagementService>(sp => new ResourceManagementService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<PgSqlStore>(),
            ProjectPaths.ResolveStorageRoot(hostEnvironment)));
        services.AddScoped<OperationQueryService>();
        services.AddScoped<SystemLogQueryService>();
        services.AddScoped<CaptureOpsService>();
        
        // 多实例水平扩展时需配置 Redis Backplane，否则 SignalR 连接仅落在单节点。
        services.AddSignalR();
        
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Token))
                        {
                            var cookieToken = context.Request.Cookies["aura_token"];
                            if (!string.IsNullOrWhiteSpace(cookieToken)) context.Token = cookieToken;
                        }
                        var path = context.HttpContext.Request.Path;
                        if (string.IsNullOrWhiteSpace(context.Token) && path.StartsWithSegments("/hubs/events", StringComparison.OrdinalIgnoreCase))
                        {
                            var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(accessToken)) context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("超级管理员", policy => policy.RequireRole("super_admin"));
            options.AddPolicy("楼栋管理员", policy => policy.RequireRole("building_admin", "super_admin"));
        });

        return services;
    }
}
