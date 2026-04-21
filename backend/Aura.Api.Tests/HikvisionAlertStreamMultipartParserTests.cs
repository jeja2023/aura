/* 文件：海康告警流解析单元测试（HikvisionAlertStreamMultipartParserTests.cs） | File: Hikvision alert stream multipart parser tests */
using System.Text;
using Aura.Api.Services.Hikvision;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aura.Api.Tests;

public sealed class HikvisionAlertStreamMultipartParserTests
{
    [Fact]
    public async Task Multipart_仅一段XML时应能提取并清空缓冲区()
    {
        var boundary = "sampleBoundary123";
        var body = Encoding.UTF8.GetBytes("<e n=\"1\"/>");
        var headers = $"Content-Type: application/xml\r\nContent-Length: {body.Length}\r\n";
        var prefix = Encoding.UTF8.GetBytes("--" + boundary + "\r\n" + headers + "\r\n");
        var buffer = new List<byte>();
        buffer.AddRange(prefix);
        buffer.AddRange(body);

        var logger = NullLogger.Instance;
        var parsed = new List<string>();
        await HikvisionAlertStreamMultipartParser.DrainBufferAsync(
            buffer,
            boundary,
            async (_, data) =>
            {
                parsed.Add(Encoding.UTF8.GetString(data));
                await Task.CompletedTask;
            },
            logger,
            CancellationToken.None);

        Assert.Single(parsed);
        Assert.Empty(buffer);
        Assert.Contains("<e", parsed[0], StringComparison.Ordinal);
    }
}

