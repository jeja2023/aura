/* 文件：服务注册编排（ServiceExtensions.cs） | File: Service registration orchestration */
using Aura.Api.Internal;
using Microsoft.Extensions.Hosting;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    public static IServiceCollection AddAuraServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment hostEnvironment, bool isDev)
    {
        if (hostEnvironment.IsProduction())
        {
            EnsureSafeProductionConfiguration(configuration);
        }

        var isTesting = hostEnvironment.IsEnvironment("Testing");
        var jwtKey = configuration["Jwt:Key"];
        var jwtIssuer = configuration["Jwt:Issuer"] ?? (isTesting ? "Aura.Api.Testing" : "Aura.Api");
        var jwtAudience = configuration["Jwt:Audience"] ?? (isTesting ? "Aura.Client.Testing" : "Aura.Client");

        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            if (!isDev && !isTesting)
            {
                throw new InvalidOperationException("JWT Key 未配置（生产环境必须配置）");
            }

            jwtKey = isTesting
                ? "aura-integration-test-jwt-signing-key-min-32-chars"
                : "aura-dev-jwt-key-please-change";
        }

        var jwtExpireMinutes = int.TryParse(configuration["Jwt:ExpireMinutes"], out var jm) ? jm : 480;
        var pgsqlConn = configuration.GetConnectionString("PgSql") ?? "";
        var redisConn = configuration.GetConnectionString("Redis") ?? "";
        var alertWebhookUrl = configuration["Ops:Alert:WebhookUrl"];
        var alertNotifyFilePath = ProjectPaths.ResolvePathRelativeToProjectRoot(hostEnvironment, configuration["Ops:Alert:FilePath"]);

        AddAuraPersistence(services, pgsqlConn, redisConn);
        AddAuraAlertNotifier(services, configuration, alertWebhookUrl, alertNotifyFilePath);
        AddAuraHostedServices(services);
        AddAuraAiClient(services, configuration);
        AddMediaAnalysisServices(services, configuration);
        AddVectorIndexServices(services, configuration);
        AddGraphServices(services, configuration);
        AddAuraApplicationServices(services, configuration, hostEnvironment, jwtKey, jwtIssuer, jwtAudience, jwtExpireMinutes);
        AddAuraHikvisionServices(services, configuration);
        AddAuraRateLimiting(services);
        AddAuraSignalR(services, configuration, redisConn);
        AddAuraAuthenticationAndAuthorization(services, jwtKey, jwtIssuer, jwtAudience);

        return services;
    }
}
