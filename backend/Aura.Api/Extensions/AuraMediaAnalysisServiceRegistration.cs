using Aura.Api.MediaAnalysis;
using Microsoft.Extensions.Http.Resilience;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddMediaAnalysisServices(IServiceCollection services, IConfiguration configuration)
    {
        var totalTimeout = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("MediaAnalysis:Http:TotalTimeoutSeconds", 120)));
        var attemptTimeout = TimeSpan.FromSeconds(Math.Max(2, configuration.GetValue("MediaAnalysis:Http:AttemptTimeoutSeconds", 30)));
        var maxRetries = Math.Clamp(configuration.GetValue("MediaAnalysis:Http:MaxRetryAttempts", 2), 0, 10);

        void AddProviderResilience(IHttpClientBuilder builder)
        {
            builder.AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = totalTimeout;
                options.AttemptTimeout.Timeout = attemptTimeout;
                options.Retry.MaxRetryAttempts = maxRetries;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(60, attemptTimeout.TotalSeconds * 2 + 10));
            });
        }

        var providerClient = services.AddHttpClient("MediaAnalysisProvider")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1,
                    configuration.GetValue("MediaAnalysis:Http:ConnectTimeoutSeconds", 10)))
            });
        AddProviderResilience(providerClient);

        var mtlsClient = services.AddHttpClient("MediaAnalysisProviderMtls")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var certificatePath = configuration["MediaAnalysis:Http:Mtls:CertificatePath"]?.Trim();
                if (string.IsNullOrWhiteSpace(certificatePath))
                    throw new InvalidOperationException("MediaAnalysis:Http:Mtls:CertificatePath is required for mTLS providers.");
                var passwordEnvironment = configuration["MediaAnalysis:Http:Mtls:CertificatePasswordEnvironmentVariable"]?.Trim();
                var password = string.IsNullOrWhiteSpace(passwordEnvironment)
                    ? configuration["MediaAnalysis:Http:Mtls:CertificatePassword"]
                    : Environment.GetEnvironmentVariable(passwordEnvironment);
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    Path.GetFullPath(certificatePath),
                    password,
                    X509KeyStorageFlags.EphemeralKeySet);
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1,
                        configuration.GetValue("MediaAnalysis:Http:ConnectTimeoutSeconds", 10))),
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        ClientCertificates = new X509CertificateCollection { certificate }
                    }
                };
            });
        AddProviderResilience(mtlsClient);

        services.AddHttpClient("MediaAnalysisOAuth")
            .ConfigureHttpClient(client => client.Timeout = totalTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1,
                    configuration.GetValue("MediaAnalysis:Http:ConnectTimeoutSeconds", 10)))
            });

        services.AddHttpClient("MediaArtifact")
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1,
                    configuration.GetValue("MediaAnalysis:Artifacts:ConnectTimeoutSeconds", 10)))
            });

        services.AddSingleton<ISecretReferenceResolver, ConfigurationSecretReferenceResolver>();
        services.AddSingleton<MediaAnalysisOutboundUrlPolicy>();
        services.AddSingleton<OAuthClientCredentialsTokenProvider>();
        services.AddSingleton<IMediaAnalysisProviderResolver, MediaAnalysisProviderResolver>();
        services.AddSingleton<MediaAnalysisRepository>();
        services.AddSingleton<TenantScopeAccessService>();
        services.AddSingleton<MediaAnalysisBusinessProjector>();
        services.AddSingleton<InboxRepository>();
        services.AddSingleton<MediaAnalysisOrchestrator>();
        services.AddSingleton<MediaAnalysisJobMonitorRepository>();
        services.AddSingleton<MediaAnalysisWebhookVerifier>();
        services.AddSingleton<BackgroundWorkerHeartbeat>();
        services.AddSingleton<MediaArtifactRepository>();
        services.AddSingleton<MediaArtifactArchiveService>();
        if (configuration.GetValue("MediaAnalysis:Workers:Enabled", false))
        {
            services.AddHostedService<MediaAnalysisJobHostedService>();
            services.AddHostedService<MediaAnalysisJobMonitorHostedService>();
            services.AddHostedService<SubscriptionReconcilerHostedService>();
            services.AddHostedService<InboxProcessorHostedService>();
            if (configuration.GetValue("MediaAnalysis:Artifacts:Enabled", true))
                services.AddHostedService<MediaArtifactArchiveHostedService>();
        }
    }
}
