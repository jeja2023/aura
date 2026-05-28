using System.Net;
using System.Text;
using Aura.Api.Ai;
using Aura.Api.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aura.Api.Tests;

public sealed class AiClientTests
{
    [Fact]
    public void ResolveBaseUrls_BaseUrlsShouldOverrideSingleBaseUrl()
    {
        var urls = AiClient.ResolveBaseUrls(
            " http://ai-0.local:8000 ; http://ai-1.local:8000/ ",
            "http://single.local:8000");

        Assert.Equal(["http://ai-0.local:8000", "http://ai-1.local:8000"], urls);
    }

    [Fact]
    public void ResolveBaseUrls_ShouldSupportLineSeparatedValues()
    {
        var urls = AiClient.ResolveBaseUrls(
            "http://ai-0.local:9001\r\nhttp://ai-1.local:9002\nhttp://ai-2.local:9003",
            null);

        Assert.Equal([
            "http://ai-0.local:9001",
            "http://ai-1.local:9002",
            "http://ai-2.local:9003"
        ], urls);
    }

    [Fact]
    public void ResolveBaseUrls_ShouldRejectInvalidUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            AiClient.ResolveBaseUrls("http://ai.local:8000/path?x=1", null));
    }

    [Fact]
    public async Task ExtractAsync_Http200ButCodeNonZero_ShouldFail()
    {
        using var client = CreateClient("""
            {"code":50001,"msg":"extract failed","data":{"feature":[],"dim":0}}
            """);
        var sut = new AiClient(client, "http://ai.local", NullLogger<AiClient>.Instance);

        var result = await sut.ExtractAsync("base64", "{}");

        Assert.False(result.Success);
        Assert.Contains("extract failed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_Http200ButCodeNonZero_ShouldFail()
    {
        using var client = CreateClient("""
            {"code":42901,"msg":"rate limited","data":[]}
            """);
        var sut = new AiClient(client, "http://ai.local", NullLogger<AiClient>.Instance);

        var result = await sut.SearchAsync([1f, 2f], 10);

        Assert.False(result.Success);
        Assert.Empty(result.Items);
        Assert.Contains("rate limited", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_CodeZeroEmptyList_ShouldRemainSuccessful()
    {
        using var client = CreateClient("""
            {"code":0,"msg":"ok","data":[]}
            """);
        var sut = new AiClient(client, "http://ai.local", NullLogger<AiClient>.Instance);

        var result = await sut.SearchAsync([1f, 2f], 10);

        Assert.True(result.Success);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task UpsertAsync_Http200ButCodeNonZero_ShouldFail()
    {
        using var client = CreateClient("""
            {"code":50301,"msg":"storage unavailable","data":{"vid":"C_1","engine":"unavailable"}}
            """);
        var sut = new AiClient(client, "http://ai.local", NullLogger<AiClient>.Instance);

        var result = await sut.UpsertAsync("C_1", [1f, 2f]);

        Assert.False(result.Success);
        Assert.Equal("unavailable", result.Engine);
        Assert.Contains("storage unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSearchStatsAsync_ShouldReadWindowMetrics()
    {
        using var client = CreateClient("""
            {
              "code":0,
              "msg":"ok",
              "data":{
                "search_total":12,
                "search_success":10,
                "search_failed":2,
                "search_empty":3,
                "search_avg_latency_ms":18.5,
                "window":{
                  "window_minutes":15,
                  "search_total":4,
                  "search_success":3,
                  "search_failed":1,
                  "search_empty":1,
                  "search_avg_latency_ms":22.7
                }
              }
            }
            """);
        var sut = new AiClient(client, "http://ai.local", NullLogger<AiClient>.Instance);

        var result = await sut.GetSearchStatsAsync(15);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(12, result.Data!.search_total);
        Assert.NotNull(result.Data.window);
        Assert.Equal(15, result.Data.window!.window_minutes);
        Assert.Equal(1, result.Data.window.search_failed);
    }

    [Fact]
    public async Task ExtractAsync_MultipleBaseUrls_ShouldRoundRobin()
    {
        var requestedHosts = new List<string>();
        using var client = CreateClient(request =>
        {
            requestedHosts.Add(request.RequestUri!.Authority);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"code":0,"msg":"ok","data":{"feature":[1,0],"dim":2}}
                    """, Encoding.UTF8, "application/json")
            });
        });
        var sut = new AiClient(client, ["http://ai-0.local:8000", "http://ai-1.local:8000"], NullLogger<AiClient>.Instance);

        _ = await sut.ExtractAsync("base64", "{}");
        _ = await sut.ExtractAsync("base64", "{}");
        _ = await sut.ExtractAsync("base64", "{}");

        Assert.Equal(["ai-0.local:8000", "ai-1.local:8000", "ai-0.local:8000"], requestedHosts);
    }

    [Fact]
    public async Task ExtractAsync_FirstEndpointServerError_ShouldFailOver()
    {
        var requestedHosts = new List<string>();
        using var client = CreateClient(request =>
        {
            requestedHosts.Add(request.RequestUri!.Authority);
            if (request.RequestUri!.Authority == "ai-0.local:8000")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"code":50301,"msg":"busy"}""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"code":0,"msg":"ok","data":{"feature":[1,0],"dim":2}}
                    """, Encoding.UTF8, "application/json")
            });
        });
        var sut = new AiClient(client, ["http://ai-0.local:8000", "http://ai-1.local:8000"], NullLogger<AiClient>.Instance);

        var result = await sut.ExtractAsync("base64", "{}");

        Assert.True(result.Success);
        Assert.Equal(["ai-0.local:8000", "ai-1.local:8000"], requestedHosts);
    }

    [Fact]
    public async Task GetClusterHealthAsync_ShouldReportEachEndpoint()
    {
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.Authority == "ai-0.local:8000")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":0,"msg":"ok","model_loaded":true}""", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"msg":"loading","model_loaded":false}""", Encoding.UTF8, "application/json")
            });
        });
        var sut = new AiClient(client, ["http://ai-0.local:8000", "http://ai-1.local:8000"], NullLogger<AiClient>.Instance);

        var health = await sut.GetClusterHealthAsync();

        Assert.Equal(2, health.ConfiguredNodeCount);
        Assert.Equal(2, health.ReachableNodeCount);
        Assert.Equal(1, health.ModelLoadedNodeCount);
        Assert.True(health.AnyModelLoaded);
    }

    [Fact]
    public async Task ExtractAsync_RuntimeBaseUrls_ShouldOverrideFallbackWithoutRestart()
    {
        var requestedHosts = new List<string>();
        using var client = CreateClient(request =>
        {
            requestedHosts.Add(request.RequestUri!.Authority);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"code":0,"msg":"ok","data":{"feature":[1,0],"dim":2}}
                    """, Encoding.UTF8, "application/json")
            });
        });
        var config = new FakeSystemConfigRepository("http://runtime-ai-0.local:9001;http://runtime-ai-1.local:9002");
        var provider = new AiRuntimeOptionsProvider(
            config,
            ["http://fallback-ai.local:8000"],
            NullLogger<AiRuntimeOptionsProvider>.Instance,
            TimeSpan.Zero);
        var sut = new AiClient(client, provider, NullLogger<AiClient>.Instance);

        _ = await sut.ExtractAsync("base64", "{}");
        config.Value = "http://runtime-ai-2.local:9003";
        provider.Invalidate();
        _ = await sut.ExtractAsync("base64", "{}");

        Assert.Equal(["runtime-ai-0.local:9001", "runtime-ai-2.local:9003"], requestedHosts);
    }

    private static HttpClient CreateClient(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        return new HttpClient(new StubHttpMessageHandler(handler));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }

    private sealed class FakeSystemConfigRepository(string value)
        : SystemConfigRepository(new PgSqlConnectionFactory("Host=localhost;Database=aura;Username=test;Password=test"), null)
    {
        public string Value { get; set; } = value;

        public override Task<DbSystemConfig?> GetAsync(string configKey)
        {
            return Task.FromResult<DbSystemConfig?>(new DbSystemConfig(configKey, Value, "tester", DateTimeOffset.Now));
        }
    }
}
