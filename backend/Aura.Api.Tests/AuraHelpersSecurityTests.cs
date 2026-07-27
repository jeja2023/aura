using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aura.Api.Cache;
using Aura.Api.Capture;
using Aura.Api.Data;
using Aura.Api.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aura.Api.Tests;

public sealed class AuraHelpersSecurityTests
{
    [Fact]
    public void VerifyHmac接受合法十六进制签名并忽略大小写()
    {
        const string payload = "1|2|2026-07-03T12:00:00.0000000+08:00";
        const string secret = "test-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToUpperInvariant();

        Assert.True(AuraHelpers.VerifyHmac(payload, signature, secret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-signature")]
    [InlineData("001122")]
    public void VerifyHmac拒绝非法签名(string signature)
    {
        Assert.False(AuraHelpers.VerifyHmac("payload", signature, "secret"));
    }

    [Fact]
    public void IsIpAllowed支持精确匹配Ipv4MappedIpv6和Cidr()
    {
        var whitelist = new[] { "10.1.0.0/16", "192.168.1.20", "::1" };

        Assert.True(AuraHelpers.IsIpAllowed(IPAddress.Parse("10.1.2.3"), whitelist));
        Assert.True(AuraHelpers.IsIpAllowed(IPAddress.Parse("::ffff:192.168.1.20"), whitelist));
        Assert.True(AuraHelpers.IsIpAllowed(IPAddress.IPv6Loopback, whitelist));
        Assert.False(AuraHelpers.IsIpAllowed(IPAddress.Parse("10.2.2.3"), whitelist));
    }

    [Fact]
    public async Task CheckRateLimitAsync在Redis不可用时使用本地降级计数()
    {
        using var provider = new RedisConnectionProvider(null, NullLogger<RedisConnectionProvider>.Instance);
        var cache = new RedisCacheService(provider, NullLogger<RedisCacheService>.Instance);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var bucket = "test.local." + Guid.NewGuid().ToString("N");

        var first = await AuraHelpers.CheckRateLimitAsync(context.Request, cache, bucket, 1, TimeSpan.FromMinutes(1));
        var second = await AuraHelpers.CheckRateLimitAsync(context.Request, cache, bucket, 1, TimeSpan.FromMinutes(1));

        Assert.Null(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ReadCaptureRequestBodyAsync按原始字节计算摘要()
    {
        const string json = "{\"deviceId\":7,\"channelNo\":2,\"imageBase64\":\"dGVzdA==\"}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        var result = await AuraHelpers.ReadCaptureRequestBodyAsync(context.Request, 4096, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(7, result.Payload.GetProperty("deviceId").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.BodySha256);
    }

    [Fact]
    public async Task ValidateCaptureRequest拒绝重复Nonce()
    {
        using var provider = new RedisConnectionProvider(null, NullLogger<RedisConnectionProvider>.Instance);
        var cache = new RedisCacheService(provider, NullLogger<RedisCacheService>.Instance);
        var devices = new DeviceRepository(new PgSqlConnectionFactory(string.Empty), NullLogger<DeviceRepository>.Instance);
        var context = new DefaultHttpContext();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        const string bodySha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string secret = "capture-security-test-secret";
        var payload = new CapturePayload
        {
            DeviceId = RandomNumberGenerator.GetInt32(10000, 100000),
            ChannelNo = 1,
            CaptureTime = DateTimeOffset.UtcNow,
            ImageBase64 = "dGVzdA==",
            MetadataJson = "{}"
        };
        var canonical = AuraHelpers.BuildCaptureSignaturePayload(payload.DeviceId, payload.ChannelNo, timestamp, nonce, bodySha256);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        context.Request.Headers["X-Capture-Timestamp"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Request.Headers["X-Capture-Nonce"] = nonce;
        context.Request.Headers["X-Signature"] = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

        var first = await AuraHelpers.ValidateCaptureRequest(
            context.Request, payload, bodySha256, devices, true, secret, null,
            4096, 1024, 1024, 300, cache);
        var replay = await AuraHelpers.ValidateCaptureRequest(
            context.Request, payload, bodySha256, devices, true, secret, null,
            4096, 1024, 1024, 300, cache);

        Assert.Null(first);
        Assert.NotNull(replay);
    }
}
