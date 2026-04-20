/* 文件：海康 alertStream multipart 解析（HikvisionAlertStreamMultipartParser.cs） | File: Hikvision alertStream multipart parser */
using System.Text;

namespace Aura.Api.Services.Hikvision;

/// <summary>对齐官方 AppsDemo <c>ParseAlarmData</c>：自 <c>multipart/mixed</c> 缓冲中提取完整部件。</summary>
internal static class HikvisionAlertStreamMultipartParser
{
    private static readonly byte[] DoubleCrLf = "\r\n\r\n"u8.ToArray();

    /// <summary>丢弃缓冲前的噪声直至出现分段边界或数据不足。</summary>
    public static void TrimPreamble(List<byte> buffer, string boundary, ILogger logger)
    {
        if (buffer.Count == 0 || string.IsNullOrWhiteSpace(boundary))
        {
            return;
        }

        var boundaryMarker = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
        var data = buffer.ToArray();
        var idx = IndexOf(data, 0, boundaryMarker);
        if (idx > 0 && idx < 65536)
        {
            logger.LogDebug("海康告警流跳过前导噪声字节。length={Length}", idx);
            buffer.RemoveRange(0, idx);
        }
    }

    /// <summary>若缓冲区首部有完整 multipart 段则解析并移除；返回是否处理了一段。</summary>
    public static bool TryRemoveNextPart(
        List<byte> buffer,
        string boundary,
        ILogger logger,
        out string contentTypeLine,
        out byte[] body)
    {
        contentTypeLine = "";
        body = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(boundary) || buffer.Count == 0)
        {
            return false;
        }

        var boundaryMarker = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
        if (buffer.Count < boundaryMarker.Length)
        {
            return false;
        }

        var data = buffer.ToArray();
        var partStart = IndexOf(data, 0, boundaryMarker);
        if (partStart < 0)
        {
            return false;
        }

        if (partStart > 0)
        {
            return false;
        }

        var headersBegin = partStart + boundaryMarker.Length;
        var headerEnd = IndexOf(data, headersBegin, DoubleCrLf);
        if (headerEnd < 0)
        {
            return false;
        }

        var headersText = Encoding.UTF8.GetString(data, headersBegin, headerEnd - headersBegin);
        if (!TryParseContentLength(headersText, out var bodyLen) || bodyLen < 0)
        {
            return false;
        }

        var bodyStart = headerEnd + DoubleCrLf.Length;
        var partEnd = bodyStart + bodyLen;
        if (partEnd > data.Length)
        {
            return false;
        }

        contentTypeLine = ExtractContentTypeLine(headersText);
        body = new byte[bodyLen];
        Buffer.BlockCopy(data, bodyStart, body, 0, bodyLen);
        buffer.RemoveRange(0, partEnd);
        return true;
    }

    public static async Task DrainBufferAsync(
        List<byte> buffer,
        string boundary,
        Func<string, byte[], Task> onPartAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        TrimPreamble(buffer, boundary, logger);
        while (TryRemoveNextPart(buffer, boundary, logger, out var ctLine, out var body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await onPartAsync(ctLine, body).ConfigureAwait(false);
        }
    }

    private static bool TryParseContentLength(string headersBlock, out int length)
    {
        length = 0;
        foreach (var rawLine in headersBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var v = line["Content-Length:".Length..].Trim();
            return int.TryParse(v, out length);
        }

        return false;
    }

    private static string ExtractContentTypeLine(string headersBlock)
    {
        foreach (var rawLine in headersBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return "";
    }

    private static int IndexOf(byte[] src, int index, byte[] value)
    {
        if (src.Length == 0 || value.Length == 0 || index < 0 || index > src.Length - value.Length)
        {
            return -1;
        }

        var end = src.Length - value.Length;
        for (var i = index; i <= end; i++)
        {
            if (src[i] != value[0])
            {
                continue;
            }

            var match = true;
            for (var j = 1; j < value.Length; j++)
            {
                if (src[i + j] != value[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
