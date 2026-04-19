/* 文件：海康 ISAPI 路径白名单单元测试 | File: Hikvision ISAPI path guard unit tests */
using Aura.Api.Services.Hikvision;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class HikvisionIsapiPathGuardTests
{
    [Theory]
    [InlineData("/ISAPI/System/deviceInfo", true)]
    [InlineData("/ISAPI/SDT/pictureUpload", true)]
    [InlineData("/ISAPI/Event/notification/alertStream", true)]
    [InlineData("/ISAPI/System/deviceInfo?format=json", true)]
    [InlineData("", false)]
    [InlineData("/api/foo", false)]
    [InlineData("/ISAPI/../System/deviceInfo", false)]
    [InlineData("/ISAPI/UnknownModule/x", false)]
    public void TryValidate_白名单与格式(string path, bool expectOk)
    {
        var ok = HikvisionIsapiPathGuard.TryValidate(path, 2048, out var error);
        Assert.Equal(expectOk, ok);
        if (!expectOk)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void TryValidate_路径超长失败()
    {
        var longPath = "/ISAPI/System/" + new string('a', 3000);
        var ok = HikvisionIsapiPathGuard.TryValidate(longPath, 100, out _);
        Assert.False(ok);
    }
}
