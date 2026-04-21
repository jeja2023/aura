/* 文件：海康 ISAPI 配置校验单元测试 | File: Hikvision ISAPI options validator unit tests */
using Aura.Api.Services.Hikvision;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class HikvisionIsapiOptionsValidatorTests
{
    [Fact]
    public void Validate_默认选项_成功()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Development));
        var r = v.Validate(null, new HikvisionIsapiOptions());
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void Validate_生产环境跳过Tls但未显式允许_失败()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Production));
        var o = new HikvisionIsapiOptions
        {
            SkipSslCertificateValidation = true,
            AllowInsecureDeviceTls = false
        };
        var r = v.Validate(null, o);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Validate_生产环境跳过Tls且显式允许_成功()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Production));
        var o = new HikvisionIsapiOptions
        {
            SkipSslCertificateValidation = true,
            AllowInsecureDeviceTls = true,
            GatewayMaxRequestsPerMinute = 60,
            DeviceApiMaxRequestsPerMinute = 120
        };
        var r = v.Validate(null, o);
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void Validate_生产环境限流配置为0_失败()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Production));
        var o = new HikvisionIsapiOptions
        {
            GatewayMaxRequestsPerMinute = 0,
            DeviceApiMaxRequestsPerMinute = 0
        };
        var r = v.Validate(null, o);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Validate_RequestTimeoutSeconds_越界_失败()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Development));
        var o = new HikvisionIsapiOptions { RequestTimeoutSeconds = 0 };
        var r = v.Validate(null, o);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Validate_ConnectivityProbeTimeoutSeconds_越界_失败()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Development));
        var o = new HikvisionIsapiOptions { ConnectivityProbeTimeoutSeconds = 601 };
        var r = v.Validate(null, o);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Validate_AlertStream_MaxImageBytes_越界_失败()
    {
        var v = new HikvisionIsapiOptionsValidator(new StubHostEnvironment(Environments.Development));
        var o = new HikvisionIsapiOptions
        {
            AlertStream = new HikvisionAlertStreamOptions
            {
                Enabled = true,
                MaxImageBytes = 0
            }
        };
        var r = v.Validate(null, o);
        Assert.False(r.Succeeded);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string ApplicationName { get; set; } = "Aura.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Path.GetTempPath());
        public string EnvironmentName { get; set; } = string.Empty;
    }
}
