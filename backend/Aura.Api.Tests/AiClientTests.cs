using System.Net;
using System.Text;
using Aura.Api.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aura.Api.Tests;

public sealed class AiClientTests
{
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

    private static HttpClient CreateClient(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));
        return new HttpClient(handler);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
