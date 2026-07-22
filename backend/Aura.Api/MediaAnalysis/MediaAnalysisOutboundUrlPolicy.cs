using System.Net;
using System.Net.Sockets;

namespace Aura.Api.MediaAnalysis;

internal sealed class MediaAnalysisOutboundUrlPolicy(IConfiguration configuration)
{
    private const string Section = "MediaAnalysis:OutboundSecurity";

    public async Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken)
    {
        var uri = ValidateSyntax(value);
        return await ValidateResolvedAsync(uri, cancellationToken);
    }

    public async Task<Uri> ValidateArtifactUriAsync(string value, CancellationToken cancellationToken)
    {
        var uri = ValidateHttpUri(value, allowQuery: true, "Artifact URL");
        return await ValidateResolvedAsync(uri, cancellationToken);
    }

    private async Task<Uri> ValidateResolvedAsync(Uri uri, CancellationToken cancellationToken)
    {
        var allowList = configuration.GetSection($"{Section}:AllowedHosts").Get<string[]>() ?? [];
        var explicitlyAllowed = allowList.Any(pattern => HostMatches(uri.Host, pattern));
        if (allowList.Length > 0 && !explicitlyAllowed)
        {
            throw new InvalidDataException("Provider host is not in the configured allowlist.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new InvalidDataException("Provider host could not be resolved.", ex);
        }
        if (addresses.Length == 0)
        {
            throw new InvalidDataException("Provider host did not resolve to an IP address.");
        }

        var allowLoopback = configuration.GetValue($"{Section}:AllowLoopback", false);
        var allowPrivate = configuration.GetValue($"{Section}:AllowPrivateNetworks", false);
        foreach (var address in addresses)
        {
            if (IPAddress.IsLoopback(address))
            {
                if (!allowLoopback && !explicitlyAllowed)
                    throw new InvalidDataException("Provider host resolves to a loopback address blocked by policy.");
                continue;
            }

            if (IsRestrictedAddress(address) && !allowPrivate && !explicitlyAllowed)
            {
                throw new InvalidDataException("Provider host resolves to a private or reserved address blocked by policy.");
            }
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var loopbackOnly = addresses.All(IPAddress.IsLoopback);
            var allowHttp = configuration.GetValue($"{Section}:AllowHttp", false)
                || (loopbackOnly && configuration.GetValue($"{Section}:AllowHttpForLoopback", false));
            if (!allowHttp)
                throw new InvalidDataException("Provider endpoints must use HTTPS under the current outbound policy.");
        }

        return uri;
    }

    internal static Uri ValidateSyntax(string value)
    {
        return ValidateHttpUri(value, allowQuery: false, "Provider base URL");
    }

    private static Uri ValidateHttpUri(string value, bool allowQuery, string label)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{label} must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException($"{label} must not contain inline credentials.");
        if ((!allowQuery && !string.IsNullOrEmpty(uri.Query)) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException($"{label} must not contain a query string or fragment.");
        return uri;
    }

    internal static bool IsRestrictedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6Multicast
                || address.IsIPv6SiteLocal
                || (bytes[0] & 0xfe) == 0xfc;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 0
            || octets[0] == 10
            || octets[0] == 127
            || octets[0] >= 224
            || (octets[0] == 100 && octets[1] is >= 64 and <= 127)
            || (octets[0] == 169 && octets[1] == 254)
            || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 198 && octets[1] is 18 or 19);
    }

    private static bool HostMatches(string host, string pattern)
    {
        var normalized = pattern?.Trim().TrimEnd('.') ?? string.Empty;
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalized[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }
        return string.Equals(host.TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase);
    }
}
