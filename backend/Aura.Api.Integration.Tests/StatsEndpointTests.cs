using System.Net;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class StatsEndpointTests : IClassFixture<AuraApiFactory>
{
    private readonly AuraApiFactory _factory;

    public StatsEndpointTests(AuraApiFactory factory) => _factory = factory;

    [Fact]
    public async Task OverviewEndpoint_ShouldReturnAiOperationsSummary()
    {
        var client = _factory.CreateClient();
        var token = TestingJwt.CreateToken(role: "building_admin");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stats/overview");
        request.Headers.Add("Cookie", $"aura_token={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, payload.RootElement.GetProperty("code").GetInt32());
        var data = payload.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("ai", out var ai));
        Assert.True(ai.TryGetProperty("failureRate", out _));
        Assert.True(ai.TryGetProperty("retryQueuePending", out _));
        Assert.True(ai.TryGetProperty("vectorIssueCount", out _));
        Assert.True(ai.TryGetProperty("searchAvailable", out _));
    }

    [Fact]
    public async Task DashboardEndpoint_ShouldReturnAiChartsPayload()
    {
        var client = _factory.CreateClient();
        var token = TestingJwt.CreateToken(role: "building_admin");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stats/dashboard");
        request.Headers.Add("Cookie", $"aura_token={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, payload.RootElement.GetProperty("code").GetInt32());
        var data = payload.RootElement.GetProperty("data");
        Assert.True(data.TryGetProperty("aiDaily", out var aiDaily));
        Assert.True(data.TryGetProperty("aiStatus", out var aiStatus));
        Assert.Equal(JsonValueKind.Array, aiDaily.ValueKind);
        Assert.Equal(JsonValueKind.Array, aiStatus.ValueKind);
    }
}
