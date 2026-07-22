using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aura.Api.MediaAnalysis;

internal interface IMediaAnalysisProvider
{
    Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<ProviderSubmission> AnalyzeImageAsync(JsonElement request, CancellationToken cancellationToken);
    Task<ProviderSubmission> SubmitVideoAsync(JsonElement request, CancellationToken cancellationToken);
    Task<ProviderObservedState> GetJobAsync(string externalJobId, CancellationToken cancellationToken);
    Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken);
    Task<ProviderObservedState> UpsertStreamAsync(string clientSubscriptionId, JsonElement request, CancellationToken cancellationToken);
    Task<ProviderObservedState> GetStreamAsync(string clientSubscriptionId, CancellationToken cancellationToken);
    Task StopStreamAsync(string clientSubscriptionId, CancellationToken cancellationToken);
}

internal interface IMediaAnalysisProviderResolver
{
    IMediaAnalysisProvider Resolve(MediaAnalysisProviderRecord provider);
}

internal sealed class MediaAnalysisProviderResolver(
    IHttpClientFactory httpClientFactory,
    ISecretReferenceResolver secretResolver,
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    OAuthClientCredentialsTokenProvider oauthTokenProvider,
    IConfiguration configuration,
    ILoggerFactory loggerFactory) : IMediaAnalysisProviderResolver
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerConcurrency = new(StringComparer.Ordinal);

    public IMediaAnalysisProvider Resolve(MediaAnalysisProviderRecord provider)
    {
        if (!string.Equals(provider.AdapterType, "standard_http", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported media-analysis adapter '{provider.AdapterType}'.");
        }

        return new StandardHttpMediaAnalysisProvider(
            provider,
            httpClientFactory.CreateClient(string.Equals(provider.AuthType, "mtls", StringComparison.OrdinalIgnoreCase)
                ? "MediaAnalysisProviderMtls"
                : "MediaAnalysisProvider"),
            secretResolver,
            outboundUrlPolicy,
            oauthTokenProvider,
            configuration,
            _providerConcurrency.GetOrAdd(
                $"{provider.ProviderId}:{provider.MaxConcurrency}",
                _ => new SemaphoreSlim(provider.MaxConcurrency, provider.MaxConcurrency)),
            loggerFactory.CreateLogger<StandardHttpMediaAnalysisProvider>());
    }
}

internal sealed class StandardHttpMediaAnalysisProvider(
    MediaAnalysisProviderRecord provider,
    HttpClient httpClient,
    ISecretReferenceResolver secretResolver,
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    OAuthClientCredentialsTokenProvider oauthTokenProvider,
    IConfiguration configuration,
    SemaphoreSlim concurrencyGate,
    ILogger<StandardHttpMediaAnalysisProvider> logger) : IMediaAnalysisProvider
{
    private static readonly JsonSerializerOptions JsonOptions = MediaAnalysisJson.Options;

    public Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        SendAsync<ProviderCapabilities>("capabilities", HttpMethod.Get, "/v1/capabilities", null, cancellationToken);

    public Task<ProviderSubmission> AnalyzeImageAsync(JsonElement request, CancellationToken cancellationToken) =>
        SendAsync<ProviderSubmission>("image.analyze", HttpMethod.Post, "/v1/analysis/images", request, cancellationToken);

    public Task<ProviderSubmission> SubmitVideoAsync(JsonElement request, CancellationToken cancellationToken) =>
        SendAsync<ProviderSubmission>("video.submit", HttpMethod.Post, "/v1/analysis/videos", request, cancellationToken);

    public Task<ProviderObservedState> GetJobAsync(string externalJobId, CancellationToken cancellationToken) =>
        SendAsync<ProviderObservedState>("job.get", HttpMethod.Get, $"/v1/analysis/jobs/{Uri.EscapeDataString(externalJobId)}", null, cancellationToken);

    public async Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken) =>
        _ = await SendAsync<JsonElement>("job.cancel", HttpMethod.Post, $"/v1/analysis/jobs/{Uri.EscapeDataString(externalJobId)}/cancel", new { }, cancellationToken);

    public Task<ProviderObservedState> UpsertStreamAsync(string clientSubscriptionId, JsonElement request, CancellationToken cancellationToken) =>
        SendAsync<ProviderObservedState>("stream.upsert", HttpMethod.Put, $"/v1/analysis/streams/{Uri.EscapeDataString(clientSubscriptionId)}", request, cancellationToken);

    public Task<ProviderObservedState> GetStreamAsync(string clientSubscriptionId, CancellationToken cancellationToken) =>
        SendAsync<ProviderObservedState>("stream.get", HttpMethod.Get, $"/v1/analysis/streams/{Uri.EscapeDataString(clientSubscriptionId)}", null, cancellationToken);

    public async Task StopStreamAsync(string clientSubscriptionId, CancellationToken cancellationToken) =>
        _ = await SendAsync<JsonElement>("stream.stop", HttpMethod.Delete, $"/v1/analysis/streams/{Uri.EscapeDataString(clientSubscriptionId)}", null, cancellationToken);

    private async Task<T> SendAsync<T>(string operation, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            return await SendCoreAsync<T>(operation, method, path, body, cancellationToken);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private async Task<T> SendCoreAsync<T>(string operation, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var validatedBaseUri = await outboundUrlPolicy.ValidateAsync(provider.BaseUrl, cancellationToken);
        var baseUri = new Uri(validatedBaseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        request.Headers.TryAddWithoutValidation("X-Aura-Protocol-Version", provider.ProtocolVersion);
        request.Headers.TryAddWithoutValidation("X-Aura-Provider", provider.ProviderCode);

        string? content = null;
        if (body is not null)
        {
            content = JsonSerializer.Serialize(body, JsonOptions);
            var maxRequestBytes = Math.Clamp(
                configuration.GetValue("MediaAnalysis:Http:MaxRequestBytes", 1024 * 1024),
                1024,
                16 * 1024 * 1024);
            if (Encoding.UTF8.GetByteCount(content) > maxRequestBytes)
                throw new InvalidDataException($"Provider request exceeds the {maxRequestBytes}-byte limit.");
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");
        }

        using var metric = MediaAnalysisMetrics.TrackProvider(provider.ProviderCode, operation);
        try
        {
            await ApplyAuthenticationAsync(request, content ?? string.Empty, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var responseText = await ReadResponseAsync(response, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Media-analysis provider request failed. provider={ProviderCode}, status={StatusCode}, path={Path}",
                    provider.ProviderCode,
                    (int)response.StatusCode,
                    path);
                throw new HttpRequestException(
                    $"Provider returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            if (typeof(T) == typeof(JsonElement) && string.IsNullOrWhiteSpace(responseText))
            {
                metric.Success();
                return (T)(object)JsonSerializer.SerializeToElement(new { });
            }

            var result = JsonSerializer.Deserialize<T>(responseText, JsonOptions)
                ?? throw new InvalidDataException("Provider returned an empty or invalid JSON response.");
            metric.Success();
            return result;
        }
        catch (Exception ex)
        {
            metric.Failure(ex, cancellationToken.IsCancellationRequested);
            throw;
        }
    }

    private async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Clamp(
            configuration.GetValue("MediaAnalysis:Http:MaxResponseBytes", 4 * 1024 * 1024),
            1024,
            32 * 1024 * 1024);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"Provider response exceeds the {maxBytes}-byte limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBytes, (int)(response.Content.Headers.ContentLength ?? 0)));
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"Provider response exceeds the {maxBytes}-byte limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private async Task ApplyAuthenticationAsync(HttpRequestMessage request, string body, CancellationToken cancellationToken)
    {
        if (string.Equals(provider.AuthType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(provider.AuthType, "mtls", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var secret = await secretResolver.ResolveAsync(provider.SecretRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException($"Secret reference for provider '{provider.ProviderCode}' cannot be resolved.");
        }

        if (string.Equals(provider.AuthType, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return;
        }

        if (string.Equals(provider.AuthType, "hmac", StringComparison.OrdinalIgnoreCase))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nonce = Guid.NewGuid().ToString("N");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var canonical = $"{timestamp}\n{nonce}\n{digest}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            request.Headers.TryAddWithoutValidation("X-Aura-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-Aura-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-Aura-Signature", signature);
            return;
        }

        if (string.Equals(provider.AuthType, "oauth2_client", StringComparison.OrdinalIgnoreCase))
        {
            var token = await oauthTokenProvider.GetTokenAsync(provider, secret, cancellationToken);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return;
        }

        throw new NotSupportedException($"Authentication type '{provider.AuthType}' is not supported.");
    }
}
