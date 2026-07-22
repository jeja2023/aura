using Aura.Api;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Capture;
using Aura.Api.Clustering;
using Aura.Api.Data;
using Aura.Api.Export;
using Aura.Api.Internal;
using Aura.Api.Ops;
using Aura.Api.Services;
using Aura.Api.Services.Hikvision;
using Aura.Api.Vector;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
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

    private static void AddAuraHostedServices(IServiceCollection services)
    {
        services.AddSingleton<DailyJudgeScheduleState>();
        services.AddHostedService<DailyJudgeHostedService>();
        services.AddHostedService<ReportAutomationHostedService>();
        services.AddHostedService<HikvisionAlertStreamHostedService>();
    }

    private static void AddAuraApplicationServices(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        string jwtKey,
        string jwtIssuer,
        string jwtAudience,
        int jwtExpireMinutes)
    {
        services.AddSingleton<AppStore>();
        services.AddSingleton<FeatureClusteringService>();
        services.AddSingleton<TabularExportService>();

        services.AddScoped<IdentityAdminService>(sp =>
            new IdentityAdminService(
                sp.GetRequiredService<AppStore>(),
                sp.GetRequiredService<UserAuthRepository>(),
                sp.GetRequiredService<AuditRepository>(),
                sp.GetRequiredService<RedisCacheService>(),
                sp.GetRequiredService<ILogger<IdentityAdminService>>(),
                jwtKey,
                jwtIssuer,
                jwtAudience,
                jwtExpireMinutes));

        services.AddScoped<DeviceManagementService>();
        services.AddScoped<EventDispatchService>();
        services.AddScoped<ClusterApplicationService>();
        services.AddScoped<StatsApplicationService>();
        services.AddScoped<ReportAutomationService>();
        services.AddScoped<ExportApplicationService>(sp => new ExportApplicationService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<PgSqlConnectionFactory>(),
            sp.GetRequiredService<CaptureRepository>(),
            sp.GetRequiredService<MonitoringRepository>(),
            sp.GetRequiredService<AuditRepository>(),
            sp.GetRequiredService<UserAuthRepository>(),
            sp.GetRequiredService<TabularExportService>(),
            ProjectPaths.ResolveStorageRoot(hostEnvironment)));
        services.AddScoped<OutputApplicationService>();
        services.AddScoped<VectorApplicationService>(sp => new VectorApplicationService(
            sp.GetRequiredService<AiClient>(),
            sp.GetRequiredService<LegacyVectorBridge>(),
            sp.GetRequiredService<CaptureRepository>(),
            sp.GetRequiredService<RedisCacheService>(),
            configuration.GetValue("Limits:MaxImageBase64Chars", 5_000_000),
            configuration.GetValue("Limits:MaxMetadataJsonChars", 200_000)));
        services.AddScoped<SpaceCollisionService>();
        services.AddScoped<JudgeService>();
        services.AddScoped<MonitoringQueryService>();
        services.AddScoped<CaptureProcessingService>(sp => new CaptureProcessingService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<CaptureRepository>(),
            sp.GetRequiredService<MonitoringRepository>(),
            sp.GetRequiredService<AuditRepository>(),
            sp.GetRequiredService<RetryQueueService>(),
            sp.GetRequiredService<AiClient>(),
            sp.GetRequiredService<LegacyVectorBridge>(),
            sp.GetRequiredService<EventDispatchService>(),
            ProjectPaths.ResolveStorageRoot(hostEnvironment),
            ResolveCaptureRetryFolder(configuration, hostEnvironment),
            configuration.GetValue("CaptureRetry:PreferInlineBase64", false),
            configuration.GetValue("CaptureRetry:AllowInlineBase64Fallback", false),
            configuration.GetValue("Storage:SaveCaptureImageOnSuccess", true)));
        services.AddScoped<RetryProcessingService>();
        services.AddScoped<ResourceManagementService>(sp => new ResourceManagementService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<CampusResourceRepository>(),
            sp.GetRequiredService<CaptureRepository>(),
            sp.GetRequiredService<AuditRepository>(),
            ProjectPaths.ResolveStorageRoot(hostEnvironment)));
        services.AddScoped<OperationQueryService>();
        services.AddSingleton<MediaPlatformReadinessService>();
        services.AddScoped<SystemLogQueryService>();
        services.AddScoped<CaptureOpsService>(sp => new CaptureOpsService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<PgSqlConnectionFactory>(),
            sp.GetRequiredService<CaptureRepository>(),
            sp.GetRequiredService<MonitoringRepository>(),
            sp.GetRequiredService<AuditRepository>()));
        services.AddScoped<UserQueryService>(sp => new UserQueryService(
            sp.GetRequiredService<AppStore>(),
            sp.GetRequiredService<PgSqlConnectionFactory>(),
            sp.GetRequiredService<UserAuthRepository>()));
    }
}
