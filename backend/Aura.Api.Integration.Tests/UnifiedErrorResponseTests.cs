using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class UnifiedErrorResponseTests : IClassFixture<AuraApiFactory>
{
    private readonly AuraApiFactory _factory;

    public UnifiedErrorResponseTests(AuraApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 未登录访问受保护接口时返回统一401错误模型()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/role/list");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal(40100, root.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("msg").GetString()));
    }

    [Fact]
    public async Task 抓拍签名失败时返回统一401错误模型()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/capture/push");
        request.Headers.Add("X-Signature", "bad-signature");
        request.Content = new StringContent(
            """
            {
              "deviceId": 999,
              "channelNo": 1,
              "captureTime": "2026-04-21T10:00:00+08:00",
              "imageBase64": "dGVzdA=="
            }
            """,
            Encoding.UTF8,
            "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal(40100, root.GetProperty("code").GetInt32());
        Assert.Equal("未授权", root.GetProperty("msg").GetString());
    }
}
