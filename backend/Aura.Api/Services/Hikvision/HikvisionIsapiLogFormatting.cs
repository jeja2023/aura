/* 文件：海康 ISAPI 日志截断（HikvisionIsapiLogFormatting.cs） | File: Hikvision ISAPI log formatting */
namespace Aura.Api.Services.Hikvision;

/// <summary>海康相关日志中设备响应等字段的截断，避免单条日志过大。</summary>
internal static class HikvisionIsapiLogFormatting
{
    public static string TruncateForLog(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return "";
        }

        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}
