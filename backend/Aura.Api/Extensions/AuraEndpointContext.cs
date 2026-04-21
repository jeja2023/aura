/* 鏂囦欢锛歁inimal API 璺敱涓婁笅鏂?| File: Minimal API endpoint context */
using Microsoft.AspNetCore.Builder;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Ops;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Extensions;

internal sealed class AuraEndpointContext
{
    public AuraEndpointContext(IEndpointRouteBuilder app, IConfiguration configuration, bool isDev)
    {
        Configuration = configuration;
        IsDev = isDev;
        var pgSqlConnectionFactory = app.ServiceProvider.GetRequiredService<PgSqlConnectionFactory>();
        AllowInMemoryFallback = configuration.GetValue("Aura:AllowInMemoryDataFallback", false) && !pgSqlConnectionFactory.IsConfigured;
        PgSql = app.ServiceProvider.GetRequiredService<PgSqlStore>();
        Users = app.ServiceProvider.GetRequiredService<UserAuthRepository>();
        Devices = app.ServiceProvider.GetRequiredService<DeviceRepository>();
        Capture = app.ServiceProvider.GetRequiredService<CaptureRepository>();
        Audit = app.ServiceProvider.GetRequiredService<AuditRepository>();
        Monitoring = app.ServiceProvider.GetRequiredService<MonitoringRepository>();
        CampusResources = app.ServiceProvider.GetRequiredService<CampusResourceRepository>();
        Cache = app.ServiceProvider.GetRequiredService<RedisCacheService>();
        AlertNotifier = app.ServiceProvider.GetRequiredService<IAlertNotifier>();
        Store = app.ServiceProvider.GetRequiredService<AppStore>();
        Ai = app.ServiceProvider.GetRequiredService<AiClient>();
        ReadinessLogger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("OpsReadiness");
        GlobalHmacSecret = configuration["Security:HmacSecret"] ?? "demo-hmac-secret";
        CaptureIpWhitelist = configuration.GetSection("Security:CaptureIpWhitelist").Get<string[]>();
    }

    public IConfiguration Configuration { get; }
    public bool IsDev { get; }
    public bool AllowInMemoryFallback { get; }
    public PgSqlStore PgSql { get; }
    public UserAuthRepository Users { get; }
    public DeviceRepository Devices { get; }
    public CaptureRepository Capture { get; }
    public AuditRepository Audit { get; }
    public MonitoringRepository Monitoring { get; }
    public CampusResourceRepository CampusResources { get; }
    public RedisCacheService Cache { get; }
    public IAlertNotifier AlertNotifier { get; }
    public AppStore Store { get; }
    public AiClient Ai { get; }
    public ILogger ReadinessLogger { get; }
    public string GlobalHmacSecret { get; }
    public string[]? CaptureIpWhitelist { get; }

    public const int MaxImageBase64Chars = 5_000_000;
    public const int MaxMetadataJsonChars = 200_000;
    public const long MaxCaptureRequestBytes = 12L * 1024 * 1024;
}
