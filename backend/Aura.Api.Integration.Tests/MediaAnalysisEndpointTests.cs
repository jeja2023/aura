using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class MediaAnalysisEndpointTests : IClassFixture<AuraApiFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MediaAnalysisEndpointTests(AuraApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/media-analysis/providers")]
    [InlineData("/api/vector-index/status")]
    [InlineData("/api/graph/health")]
    public async Task NewManagementEndpointsRequireAuthentication(string path)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WebhookRejectsMissingAuthenticationHeaders()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/media-analysis/v1/events")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("X-Aura-Provider", body, StringComparison.Ordinal);
    }
}
