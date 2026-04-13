/* 文件：公开健康检查集成测试 | File: Health endpoint integration tests */
using System.Net;
using System.Net.Http;
using Aura.Api.Middleware;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class HealthEndpointTests : IClassFixture<AuraApiFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(AuraApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 健康检查接口返回成功()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("寓瞳", body, StringComparison.Ordinal);
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var cids));
        Assert.False(string.IsNullOrWhiteSpace(cids?.FirstOrDefault()));
    }

    [Fact]
    public async Task 存活探针接口返回成功且无鉴权()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("alive", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 响应包含与请求一致的关联Id()
    {
        var client = _factory.CreateClient();
        const string cid = "integration-fixed-correlation-id";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, cid);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var echoed));
        Assert.Equal(cid, echoed?.FirstOrDefault());
    }

    [Fact]
    public async Task 未登录访问根路径重定向至登录页()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("/login/", location, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("returnUrl", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 已登录携带令牌访问根路径重定向到态势首页()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = TestingJwt.CreateToken();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"aura_token={token}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("/index/", location, StringComparison.OrdinalIgnoreCase);
    }
}
