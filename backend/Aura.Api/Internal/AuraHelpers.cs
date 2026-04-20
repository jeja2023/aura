using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using Aura.Api.Cache;
using Aura.Api.Capture;
using Aura.Api.Data;
using Aura.Api.Models;

namespace Aura.Api.Internal;

internal static class AuraHelpers
{
    internal const string MustChangePasswordClaimType = "aura:must_change_password";

    public static async Task<IResult?> ValidateCaptureRequest(HttpRequest request, CapturePayload normalized, PgSqlStore db, bool isDev, string globalSecret, string[]? whitelist, long maxBytes, int maxBase64, int maxMetadata, RedisCacheService cache)
    {
        if (request.ContentLength > maxBytes) return Results.BadRequest(new { code = 40006, msg = "请求体过大" });
        if (string.IsNullOrWhiteSpace(normalized.ImageBase64)) return Results.BadRequest(new { code = 40007, msg = "图片为空" });
        if (normalized.ImageBase64.Length > maxBase64) return Results.BadRequest(new { code = 40008, msg = "图片过大" });
        if (normalized.MetadataJson?.Length > maxMetadata) return Results.BadRequest(new { code = 40009, msg = "元数据过大" });
        
        var signature = request.Headers["X-Signature"].ToString();
        var payload = $"{normalized.DeviceId}|{normalized.ChannelNo}|{normalized.CaptureTime:O}";
        var deviceSecret = await db.GetDeviceHmacSecretAsync(normalized.DeviceId);
        var secretToUse = string.IsNullOrWhiteSpace(deviceSecret) ? (isDev ? globalSecret : null) : deviceSecret;
        
        if (string.IsNullOrWhiteSpace(secretToUse) || !VerifyHmac(payload, signature, secretToUse)) return Results.Unauthorized();
        
        if (whitelist != null && whitelist.Length > 0)
        {
            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString();
            if (ip == null || !whitelist.Contains(ip)) return Results.BadRequest(new { code = 40004, msg = "IP拒绝" });
        }
        
        return await CheckRateLimitAsync(request, cache, "capture", 30, TimeSpan.FromMinutes(1), normalized.DeviceId.ToString());
    }

    public static async Task<IResult?> CheckRateLimitAsync(HttpRequest request, RedisCacheService cache, string bucket, long limit, TimeSpan window, string? explicitDimension = null)
    {
        if (!cache.Enabled) return null;
        
        string segment = explicitDimension != null ? "d:" + Sanitize(explicitDimension) : (request.HttpContext.User.Identity?.IsAuthenticated == true ? "u:" + Sanitize(request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anon") : "ip:" + Sanitize(request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
        
        var count = await cache.TryConsumeFixedWindowAsync($"aura:rl:{bucket}:{segment}", window, limit);
        return count > limit ? Results.Json(new { code = 42901, msg = "请求过多" }, statusCode: 429) : null;
    }

    public static string Sanitize(string s) => s.Replace(':', '_').Replace('\r', '_').Replace('\n', '_').Trim();

    public static bool VerifyHmac(string payload, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var local = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return string.Equals(local, signature.Trim().ToLowerInvariant(), StringComparison.Ordinal);
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
}
