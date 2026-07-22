using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aura.Api.MediaAnalysis;

internal sealed class OAuthClientCredentialsTokenProvider(
    IHttpClientFactory httpClientFactory,
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, CachedOAuthToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<string> GetTokenAsync(
        MediaAnalysisProviderRecord provider,
        string secretMaterial,
        CancellationToken cancellationToken)
    {
        var credential = ParseCredential(secretMaterial);
        var tokenUri = await outboundUrlPolicy.ValidateAsync(credential.TokenUrl, cancellationToken);
        var cacheKey = CacheKey(provider.ProviderId, secretMaterial);
        if (TryGetCached(cacheKey, out var cached)) return cached;

        var gate = _locks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(cacheKey, out cached)) return cached;

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credential.ClientId,
                ["client_secret"] = credential.ClientSecret
            };
            if (!string.IsNullOrWhiteSpace(credential.Scope)) fields["scope"] = credential.Scope;
            if (!string.IsNullOrWhiteSpace(credential.Audience)) fields["audience"] = credential.Audience;
            request.Content = new FormUrlEncodedContent(fields);

            using var response = await httpClientFactory.CreateClient("MediaAnalysisOAuth").SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseText = await ReadLimitedAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OAuth token endpoint returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);

            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var accessTokenValue)
                ? accessTokenValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidDataException("OAuth token response does not contain access_token.");
            var expiresIn = root.TryGetProperty("expires_in", out var expiresValue) && expiresValue.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 30, 86400)
                : 300;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            _tokens[cacheKey] = new CachedOAuthToken(accessToken, expiresAt);
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static OAuthClientCredential ParseCredential(string secretMaterial)
    {
        try
        {
            using var document = JsonDocument.Parse(secretMaterial);
            var root = document.RootElement;
            var clientId = Required(root, "client_id");
            var clientSecret = Required(root, "client_secret");
            var tokenUrl = Required(root, "token_url");
            return new OAuthClientCredential(
                clientId,
                clientSecret,
                tokenUrl,
                Optional(root, "scope"),
                Optional(root, "audience"));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("OAuth secret must be a JSON object.", ex);
        }
    }

    private bool TryGetCached(string cacheKey, out string token)
    {
        if (_tokens.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            token = cached.AccessToken;
            return true;
        }
        token = string.Empty;
        return false;
    }

    private async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Clamp(configuration.GetValue("MediaAnalysis:Http:OAuthMaxResponseBytes", 64 * 1024), 1024, 1024 * 1024);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("OAuth token response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("OAuth token response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static string Required(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"OAuth secret field '{name}' is required.");
        return value.GetString()!.Trim();
    }

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string CacheKey(long providerId, string material) =>
        $"{providerId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";

    private sealed record CachedOAuthToken(string AccessToken, DateTimeOffset ExpiresAt);
}

internal sealed record OAuthClientCredential(
    string ClientId,
    string ClientSecret,
    string TokenUrl,
    string? Scope,
    string? Audience);
