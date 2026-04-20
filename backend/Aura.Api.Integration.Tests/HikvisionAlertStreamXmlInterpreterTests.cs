/* 文件：海康告警 XML 解析单元测试 | File: Hikvision alert XML interpreter unit tests */
using Aura.Api.Services.Hikvision;
using Xunit;

namespace Aura.Api.Integration.Tests;

public sealed class HikvisionAlertStreamXmlInterpreterTests
{
    [Fact]
    public void TryExtractChannelNo_包含channelID_可解析()
    {
        var xml = """
                  <?xml version="1.0" encoding="UTF-8"?>
                  <EventNotificationAlert>
                    <eventType>VMD</eventType>
                    <eventState>active</eventState>
                    <channelID>3</channelID>
                  </EventNotificationAlert>
                  """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        var ch = HikvisionAlertStreamXmlInterpreter.TryExtractChannelNo(bytes);
        Assert.Equal(3, ch);
    }

    [Fact]
    public void TryExtractChannelNo_无通道字段_返回空()
    {
        var xml = """
                  <?xml version="1.0" encoding="UTF-8"?>
                  <EventNotificationAlert>
                    <eventType>VMD</eventType>
                    <eventState>active</eventState>
                  </EventNotificationAlert>
                  """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        var ch = HikvisionAlertStreamXmlInterpreter.TryExtractChannelNo(bytes);
        Assert.Null(ch);
    }

    [Fact]
    public void TryExtractEventTime_包含eventTime_可解析()
    {
        var xml = """
                  <?xml version="1.0" encoding="UTF-8"?>
                  <EventNotificationAlert>
                    <eventType>VMD</eventType>
                    <eventState>active</eventState>
                    <eventTime>2026-04-20T12:34:56+08:00</eventTime>
                  </EventNotificationAlert>
                  """;
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        var t = HikvisionAlertStreamXmlInterpreter.TryExtractEventTime(bytes);
        Assert.NotNull(t);
        Assert.Equal(2026, t!.Value.Year);
    }
}

