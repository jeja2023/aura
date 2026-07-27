using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Product;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddAuraPersistence(IServiceCollection services, string pgsqlConn, string redisConn)
    {
        DapperTypeHandlers.Register();
        services.AddSingleton(new PgSqlConnectionFactory(pgsqlConn));
        services.AddSingleton<UserAuthRepository>(sp =>
            new UserAuthRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<UserAuthRepository>>()));
        services.AddSingleton<DeviceRepository>(sp =>
            new DeviceRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<DeviceRepository>>()));
        services.AddSingleton<CaptureRepository>(sp =>
            new CaptureRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<CaptureRepository>>()));
        services.AddSingleton<AuditRepository>(sp =>
            new AuditRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<AuditRepository>>()));
        services.AddSingleton<MonitoringRepository>(sp =>
            new MonitoringRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<MonitoringRepository>>()));
        services.AddSingleton<ExtensionRepository>(sp =>
            new ExtensionRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<ExtensionRepository>>()));
        services.AddSingleton<CampusResourceRepository>(sp =>
            new CampusResourceRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<CampusResourceRepository>>()));
        services.AddSingleton<SystemConfigRepository>(sp =>
            new SystemConfigRepository(sp.GetRequiredService<PgSqlConnectionFactory>(), sp.GetRequiredService<ILogger<SystemConfigRepository>>()));
        services.AddSingleton<PgSqlStore>(sp =>
            new PgSqlStore(
                sp.GetRequiredService<PgSqlConnectionFactory>(),
                sp.GetRequiredService<ILogger<PgSqlStore>>()));
        services.AddSingleton<EventCaseRepository>();
        services.AddSingleton<InvestigationRepository>();

        services.AddSingleton<RedisConnectionProvider>(sp =>
            new RedisConnectionProvider(redisConn, sp.GetRequiredService<ILogger<RedisConnectionProvider>>()));
        services.AddSingleton<RedisCacheService>();
        services.AddSingleton<RetryQueueService>();
    }
}

