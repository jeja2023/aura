using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aura.Api.MediaAnalysis;

internal interface ISecretReferenceResolver
{
    ValueTask<string?> ResolveAsync(string? secretReference, CancellationToken cancellationToken = default);
}

internal static partial class SecretReferenceValidator
{
    private const int MaxReferenceLength = 512;

    public static string Validate(string? secretReference)
    {
        var value = secretReference?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxReferenceLength)
            throw new InvalidDataException("Secret reference is empty or too long.");

        if (value.StartsWith("env://", StringComparison.OrdinalIgnoreCase))
        {
            var name = value["env://".Length..];
            if (!EnvironmentNamePattern().IsMatch(name))
                throw new InvalidDataException("env:// secret references must contain a valid environment variable name.");
            return value;
        }

        if (value.StartsWith("config://", StringComparison.OrdinalIgnoreCase))
        {
            var key = value["config://".Length..];
            if (!ConfigurationKeyPattern().IsMatch(key) || key.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("config:// secret references contain an invalid configuration key.");
            return value;
        }

        if (value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
        {
            var key = value["secret://".Length..];
            if (!SecretKeyPattern().IsMatch(key) || key.Contains("..", StringComparison.Ordinal) || key.Contains("//", StringComparison.Ordinal))
                throw new InvalidDataException("secret:// references contain an invalid secret key.");
            return value;
        }

        throw new InvalidDataException("Only env://, config:// and secret:// references are supported; plaintext secrets are not accepted.");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9/_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyPattern();
}

internal sealed class ConfigurationSecretReferenceResolver(IConfiguration configuration) : ISecretReferenceResolver
{
    public ValueTask<string?> ResolveAsync(string? secretReference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return ValueTask.FromResult<string?>(null);
        }

        secretReference = SecretReferenceValidator.Validate(secretReference);
        const string environmentPrefix = "env://";
        const string configurationPrefix = "config://";
        if (secretReference.StartsWith(environmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Environment.GetEnvironmentVariable(secretReference[environmentPrefix.Length..]));
        }

        if (secretReference.StartsWith(configurationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<string?>(configuration[secretReference[configurationPrefix.Length..]]);
        }

        const string secretPrefix = "secret://";
        if (secretReference.StartsWith(secretPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = secretReference[secretPrefix.Length..].Trim('/');
            if (string.IsNullOrWhiteSpace(key) || key.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Secret reference is invalid.");
            var configurationKey = "MediaAnalysis:Secrets:" + key.Replace('/', ':');
            var environmentKey = "AURA_MEDIA_SECRET_" + string.Concat(key.Select(character =>
                char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_'));
            return ValueTask.FromResult<string?>(configuration[configurationKey] ?? Environment.GetEnvironmentVariable(environmentKey));
        }

        throw new InvalidOperationException("Only env://, config:// and secret:// references are supported by the built-in resolver.");
    }
}

internal sealed class MediaAnalysisWebhookVerifier(
    MediaAnalysisRepository repository,
    ISecretReferenceResolver secretResolver,
    IConfiguration configuration)
{
    public const string ProviderHeader = "X-Aura-Provider";
    public const string TimestampHeader = "X-Aura-Timestamp";
    public const string NonceHeader = "X-Aura-Nonce";
    public const string SignatureHeader = "X-Aura-Signature";

    public async Task<MediaAnalysisProviderRecord> VerifyAsync(HttpRequest request, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var providerCode = RequiredHeader(request, ProviderHeader, 64);
        var timestampText = RequiredHeader(request, TimestampHeader, 32);
        var nonce = RequiredHeader(request, NonceHeader, 128);
        var providedSignature = RequiredHeader(request, SignatureHeader, 128);

        if (!long.TryParse(timestampText, out var timestampSeconds))
        {
            throw new WebhookAuthenticationException("Invalid timestamp.");
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampSeconds);
        var allowedSkew = TimeSpan.FromSeconds(configuration.GetValue("MediaAnalysis:Webhook:AllowedClockSkewSeconds", 300));
        if ((DateTimeOffset.UtcNow - timestamp).Duration() > allowedSkew)
        {
            throw new WebhookAuthenticationException("Webhook timestamp is outside the allowed time window.");
        }

        var bodyDigest = Convert.ToHexString(SHA256.HashData(body.Span)).ToLowerInvariant();
        var canonical = $"{timestampText}\n{nonce}\n{bodyDigest}";
        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(providedSignature);
        }
        catch (FormatException)
        {
            throw new WebhookAuthenticationException("Invalid signature encoding.");
        }

        var candidates = await repository.ListEnabledProvidersByCodeAsync(providerCode, cancellationToken);
        if (candidates.Count == 0)
            throw new WebhookAuthenticationException("Unknown or disabled provider.");
        MediaAnalysisProviderRecord? provider = null;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.WebhookAuthType, "hmac", StringComparison.OrdinalIgnoreCase)) continue;
            var secret = await secretResolver.ResolveAsync(candidate.WebhookSecretRef, cancellationToken);
            if (string.IsNullOrWhiteSpace(secret)) continue;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            if (CryptographicOperations.FixedTimeEquals(expected, supplied))
            {
                provider = candidate;
                break;
            }
        }
        if (provider is null) throw new WebhookAuthenticationException("Invalid signature.");

        var nonceAccepted = await repository.TryRegisterNonceAsync(provider.ProviderId, nonce, timestamp + allowedSkew, cancellationToken);
        if (!nonceAccepted)
        {
            throw new WebhookAuthenticationException("Webhook nonce was already used.");
        }

        return provider;
    }

    private static string RequiredHeader(HttpRequest request, string name, int maxLength)
    {
        var value = request.Headers[name].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new WebhookAuthenticationException($"Missing or invalid {name} header.");
        }

        return value;
    }
}

internal sealed class WebhookAuthenticationException(string message) : Exception(message);
