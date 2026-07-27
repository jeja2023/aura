using System.Net;
using System.Net.Http;
using Aura.Api.Internal;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class ProtectedStorageTests : IClassFixture<AuraApiFactory>
{
    private readonly AuraApiFactory factory;

    public ProtectedStorageTests(AuraApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task 匿名访问存储文件会被拒绝()
    {
        var response = await factory.CreateClient().GetAsync("/storage/uploads/floors/missing.png");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 通用存储端点不会绕过证据下载授权()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/storage/evidence-exports/evidence-1.zip");
        request.Headers.Add("Cookie", $"aura_token={TestingJwt.CreateToken()}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 需改密用户不能访问存储文件()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/storage/uploads/floors/missing.png");
        request.Headers.Add("Cookie", $"aura_token={TestingJwt.CreateToken(mustChangePassword: true)}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 楼栋管理员可以访问受控抓拍目录()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/storage/captures/retry/missing.bin");
        request.Headers.Add("Cookie", $"aura_token={TestingJwt.CreateToken(role: "building_admin")}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
