using System.Net;
using System.Net.Http;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class PasswordChangeEnforcementTests : IClassFixture<AuraApiFactory>
{
    private readonly AuraApiFactory _factory;

    public PasswordChangeEnforcementTests(AuraApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 需改密用户访问Me接口时会返回改密标记()
    {
        var client = _factory.CreateClient();
        var token = TestingJwt.CreateToken(mustChangePassword: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"aura_token={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("mustChangePassword", body, StringComparison.Ordinal);
        Assert.Contains("true", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 需改密用户访问受保护接口会被拦截()
    {
        var client = _factory.CreateClient();
        var token = TestingJwt.CreateToken(mustChangePassword: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/role/list");
        request.Headers.Add("Cookie", $"aura_token={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("40321", body, StringComparison.Ordinal);
    }
}
