/* 文件：后端辅助工具（AuraHelpers.cs） */
using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aura.Api.Cache;
using Aura.Api.Capture;
using Aura.Api.Data;
using Aura.Api.Models;

namespace Aura.Api.Internal;

internal static class AuraHelpers
{
    internal const string MustChangePasswordClaimType = "aura:must_change_password";
    private static readonly ConcurrentDictionary<string, LocalRateLimitCounter> LocalRateLimitCounters = new(StringComparer.Ordinal);

    public static async Task<IResult?> ValidateCaptureRequest(HttpRequest request, CapturePayload normalized, DeviceRepository devices, bool isDev, string globalSecret, string[]? whitelist, long maxBytes, int maxBase64, int maxMetadata, RedisCacheService cache)
    {
        if (request.ContentLength > maxBytes) return AuraApiResults.BadRequest("请求体过大", 40006);
        if (string.IsNullOrWhiteSpace(normalized.ImageBase64)) return AuraApiResults.BadRequest("图片为空", 40007);
        if (normalized.ImageBase64.Length > maxBase64) return AuraApiResults.BadRequest("图片过大", 40008);
        if (normalized.MetadataJson?.Length > maxMetadata) return AuraApiResults.BadRequest("元数据过大", 40009);

        var signature = request.Headers["X-Signature"].ToString();
        var payload = $"{normalized.DeviceId}|{normalized.ChannelNo}|{normalized.CaptureTime:O}";
        var deviceSecret = await devices.GetDeviceHmacSecretAsync(normalized.DeviceId);
        var secretToUse = string.IsNullOrWhiteSpace(deviceSecret) ? (isDev ? globalSecret : null) : deviceSecret;

        if (string.IsNullOrWhiteSpace(secretToUse) || !VerifyHmac(payload, signature, secretToUse)) return AuraApiResults.Unauthorized();

        if (whitelist != null && whitelist.Length > 0)
        {
            var ip = request.HttpContext.Connection.RemoteIpAddress;
            if (!IsIpAllowed(ip, whitelist)) return AuraApiResults.BadRequest("IP拒绝", 40004);
        }

        return await CheckRateLimitAsync(request, cache, "capture", 30, TimeSpan.FromMinutes(1), normalized.DeviceId.ToString());
    }

    public static async Task<IResult?> CheckRateLimitAsync(HttpRequest request, RedisCacheService cache, string bucket, long limit, TimeSpan window, string? explicitDimension = null)
    {
        var segment = explicitDimension != null
            ? "d:" + Sanitize(explicitDimension)
            : request.HttpContext.User.Identity?.IsAuthenticated == true
                ? "u:" + Sanitize(request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anon")
                : "ip:" + Sanitize(request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        var key = $"aura:rl:{bucket}:{segment}";
        var count = cache.Enabled
            ? await cache.TryConsumeFixedWindowAsync(key, window, limit)
            : null;
        count ??= ConsumeLocalFixedWindow(key, window);
        return count > limit ? AuraApiResults.TooManyRequests("请求过多", 42901) : null;
    }

    public static string Sanitize(string s) => s.Replace(':', '_').Replace('\r', '_').Replace('\n', '_').Trim();

    public static bool VerifyHmac(string payload, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(secret)) return false;

        byte[] remote;
        try
        {
            remote = Convert.FromHexString(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var local = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return remote.Length == local.Length && CryptographicOperations.FixedTimeEquals(local, remote);
    }

    public static bool IsIpAllowed(IPAddress? remoteIp, IEnumerable<string> whitelist)
    {
        if (remoteIp is null) return false;
        var normalizedRemote = NormalizeIp(remoteIp);
        foreach (var rawRule in whitelist)
        {
            var rule = (rawRule ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rule)) continue;

            if (rule.Contains('/', StringComparison.Ordinal))
            {
                if (IsIpInCidr(normalizedRemote, rule)) return true;
                continue;
            }

            if (IPAddress.TryParse(rule, out var exact) && NormalizeIp(exact).Equals(normalizedRemote))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress NormalizeIp(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static bool IsIpInCidr(IPAddress remoteIp, string cidr)
    {
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var networkIp)
            || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        networkIp = NormalizeIp(networkIp);
        if (networkIp.AddressFamily != remoteIp.AddressFamily)
        {
            return false;
        }

        var remoteBytes = remoteIp.GetAddressBytes();
        var networkBytes = networkIp.GetAddressBytes();
        var maxPrefixLength = networkBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (remoteBytes[i] != networkBytes[i]) return false;
        }

        if (remainingBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (remoteBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static long ConsumeLocalFixedWindow(string key, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var counter = LocalRateLimitCounters.GetOrAdd(key, _ => new LocalRateLimitCounter(now));
        lock (counter)
        {
            if (now - counter.WindowStart >= window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;
            if (LocalRateLimitCounters.Count > 8192)
            {
                PruneExpiredLocalCounters(now, window);
            }

            return counter.Count;
        }
    }

    private static void PruneExpiredLocalCounters(DateTimeOffset now, TimeSpan window)
    {
        foreach (var pair in LocalRateLimitCounters)
        {
            if (now - pair.Value.WindowStart >= window)
            {
                LocalRateLimitCounters.TryRemove(pair.Key, out _);
            }
        }
    }

    public static string ConvertRole(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return "building_admin";
        if (roleName.Contains("超级") || roleName.Equals("super_admin", StringComparison.OrdinalIgnoreCase)) return "super_admin";
        return "building_admin";
    }

    public static void AddOperationLog(AppStore store, string operatorName, string action, string detail)
    {
        store.Operations.Add(new OperationEntity(OperationId: Interlocked.Increment(ref store.OperationSeed), OperatorName: operatorName, Action: action, Detail: detail, CreatedAt: DateTimeOffset.Now));
    }

    private sealed class LocalRateLimitCounter
    {
        public LocalRateLimitCounter(DateTimeOffset windowStart)
        {
            WindowStart = windowStart;
        }

        public DateTimeOffset WindowStart { get; set; }
        public long Count { get; set; }
    }
}
