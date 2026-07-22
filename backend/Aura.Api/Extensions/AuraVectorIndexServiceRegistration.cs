using Aura.Api.Vector;
using Microsoft.Extensions.Http.Resilience;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddVectorIndexServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("LegacyArangoVector")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(5,
                    configuration.GetValue("VectorIndex:LegacyArango:TotalTimeoutSeconds", 120)));
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(2,
                    configuration.GetValue("VectorIndex:LegacyArango:AttemptTimeoutSeconds", 30)));
                options.Retry.MaxRetryAttempts = Math.Clamp(
                    configuration.GetValue("VectorIndex:LegacyArango:MaxRetryAttempts", 2), 0, 10);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });
        services.AddSingleton<PgVectorIndex>();
        services.AddSingleton<LegacyArangoVectorIndex>();
        services.AddSingleton<LegacyArangoVectorExportClient>();
        services.AddSingleton<VectorWriteCompensationRepository>();
        services.AddSingleton<VectorIndexRouter>();
        services.AddSingleton<LegacyVectorBridge>();
        services.AddSingleton<VectorMigrationService>();
        services.AddSingleton<IVectorIndex>(serviceProvider => serviceProvider.GetRequiredService<VectorIndexRouter>());
        if (configuration.GetValue("MediaAnalysis:Workers:Enabled", false)
            && configuration.GetValue("VectorIndex:Compensation:Enabled", true))
        {
            services.AddHostedService<VectorWriteCompensationHostedService>();
        }
    }
}
