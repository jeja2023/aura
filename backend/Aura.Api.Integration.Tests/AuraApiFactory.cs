/* 文件：集成测试用 Web 主机工厂 | File: WebApplicationFactory for integration tests */
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aura.Api.Integration.Tests;

/// <summary>
/// 使用 Testing 环境加载 appsettings.Testing.json：不连 Redis/PG、不跑开发库初始化，缩短启动并减少控制台噪音。
/// </summary>
public sealed class AuraApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
