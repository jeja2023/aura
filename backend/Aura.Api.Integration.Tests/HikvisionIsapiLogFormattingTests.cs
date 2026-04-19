/* 文件：海康日志截断单元测试 | File: Hikvision log formatting unit tests */
using Aura.Api.Services.Hikvision;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class HikvisionIsapiLogFormattingTests
{
    [Fact]
    public void TruncateForLog_空或零长度_返回空字符串()
    {
        Assert.Equal("", HikvisionIsapiLogFormatting.TruncateForLog(null, 10));
        Assert.Equal("", HikvisionIsapiLogFormatting.TruncateForLog("abc", 0));
    }

    [Fact]
    public void TruncateForLog_超长_截断并加省略号()
    {
        var s = new string('a', 20);
        var t = HikvisionIsapiLogFormatting.TruncateForLog(s, 5);
        Assert.Equal("aaaaa…", t);
    }
}
