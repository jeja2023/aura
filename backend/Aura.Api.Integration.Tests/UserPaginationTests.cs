using System.Net;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class UserPaginationTests : IClassFixture<AuraApiFactory>
{
    private readonly AuraApiFactory _factory;

    public UserPaginationTests(AuraApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 用户列表接口支持分页与关键字过滤()
    {
        var client = _factory.CreateClient();
        var token = TestingJwt.CreateToken();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/list?page=1&pageSize=1&keyword=admin");
        request.Headers.Add("Cookie", $"aura_token={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Single(data.EnumerateArray());

        var pager = root.GetProperty("pager");
        Assert.Equal(1, pager.GetProperty("page").GetInt32());
        Assert.Equal(1, pager.GetProperty("pageSize").GetInt32());
        Assert.True(pager.GetProperty("total").GetInt32() >= 1);

        var firstUser = data[0];
        Assert.Equal("admin", firstUser.GetProperty("userName").GetString());
    }
}
