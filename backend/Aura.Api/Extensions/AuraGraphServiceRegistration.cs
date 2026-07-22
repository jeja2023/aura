using Aura.Api.Graph;
using Microsoft.Extensions.Http.Resilience;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddGraphServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("ArangoGraph")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Graph:Arango:ConnectTimeoutSeconds", 10)))
            })
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("Graph:Arango:TotalTimeoutSeconds", 30)));
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(Math.Max(2, configuration.GetValue("Graph:Arango:AttemptTimeoutSeconds", 10)));
                options.Retry.MaxRetryAttempts = Math.Clamp(configuration.GetValue("Graph:Arango:MaxRetryAttempts", 2), 0, 10);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });
        services.AddSingleton<IGraphRepository, ArangoGraphRepository>();
        services.AddSingleton<GraphProjectionRepository>();
        services.AddSingleton<GraphQueryService>();
        services.AddSingleton<GraphRelationshipProjectionService>();
        services.AddSingleton<GraphRebuildService>();
        if (configuration.GetValue("Graph:Enabled", false))
        {
            services.AddHostedService<GraphProjectionHostedService>();
            services.AddHostedService<GraphRebuildHostedService>();
        }
    }
}
