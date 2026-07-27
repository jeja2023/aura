using System.Net;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class CommercialProductRouteTests(AuraApiFactory factory) : IClassFixture<AuraApiFactory>
{
    [Theory]
    [InlineData("/api/v1/events?tenantId=1&page=1&pageSize=20")]
    [InlineData("/api/v1/cases?tenantId=1&page=1&pageSize=20")]
    [InlineData("/api/v1/ops/center?tenantId=1")]
    [InlineData("/api/v1/analytics/dashboard?tenantId=1")]
    [InlineData("/api/v1/mobile/push-config")]
    public async Task CommercialApisRejectAnonymousRequests(string path)
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WorkbenchRouteServesTheOperationalApplicationShell()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"aura_token={TestingJwt.CreateToken()}");

        using var response = await client.GetAsync("/workbench/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("商业工作台", html, StringComparison.Ordinal);
        Assert.Contains("manifest.webmanifest", html, StringComparison.Ordinal);
        Assert.Contains("data-view=\"events\"", html, StringComparison.Ordinal);
    }
}
