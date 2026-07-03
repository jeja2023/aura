using Aura.Api.Ai;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Ops;
using Microsoft.Extensions.Http.Resilience;

namespace Aura.Api.Extensions;

public static partial class ServiceExtensions
{
    private static void AddAuraAlertNotifier(IServiceCollection services, IConfiguration configuration, string? alertWebhookUrl, string? alertNotifyFilePath)
    {
        var alertTotalTimeout = configuration.GetValue("HttpClients:AlertNotifier:TotalRequestTimeoutSeconds", 30);
        var alertAttemptTimeout = configuration.GetValue("HttpClients:AlertNotifier:AttemptTimeoutSeconds", 15);
        var alertMaxRetries = configuration.GetValue("HttpClients:AlertNotifier:MaxRetryAttempts", 2);

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
    }

    private static void AddAuraAiClient(IServiceCollection services, IConfiguration configuration)
    {
        var aiTotalTimeout = configuration.GetValue("HttpClients:Ai:TotalRequestTimeoutSeconds", 120);
        var aiAttemptTimeout = configuration.GetValue("HttpClients:Ai:AttemptTimeoutSeconds", 90);
        var aiMaxRetries = configuration.GetValue("HttpClients:Ai:MaxRetryAttempts", 2);

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
        services.AddSingleton<AiRuntimeOptionsProvider>(sp =>
        {
            var baseUrls = AiClient.ResolveBaseUrls(configuration["Ai:BaseUrls"], configuration["Ai:BaseUrl"]);
            return new AiRuntimeOptionsProvider(
                sp.GetRequiredService<SystemConfigRepository>(),
                baseUrls,
                sp.GetRequiredService<ILogger<AiRuntimeOptionsProvider>>());
        });
        services.AddSingleton<AiClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(AuraHttpClientNames.AiService);
            return new AiClient(
                client,
                sp.GetRequiredService<AiRuntimeOptionsProvider>(),
                sp.GetRequiredService<ILogger<AiClient>>());
        });
    }
}
