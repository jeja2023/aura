/* 文件：海康 ISAPI 路径校验（HikvisionIsapiPathGuard.cs） | File: Hikvision ISAPI path guard */
namespace Aura.Api.Services.Hikvision;

/// <summary>
/// 与官方 C# AppsDemo_ISAPI 中出现的 ISAPI 根命名空间对齐，仅允许这些前缀下的路径经网关转发，防止误用为开放代理。
/// </summary>
internal static class HikvisionIsapiPathGuard
{
    /// <summary>Demo 中曾出现的 /ISAPI 下二级路径（含 System、Intelligent、Smart、Traffic 等）。</summary>
    private static readonly string[] AllowedSecondSegments =
    [
        "System", "ContentMgmt", "Streaming", "Event", "Intelligent", "Security",
        "Traffic", "PTZCtrl", "SDT", "ITC", "Image", "Custom", "Thermal", "Smart"
    ];

    public static bool TryValidate(string pathAndQuery, int maxLength, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pathAndQuery))
        {
            error = "路径不能为空";
            return false;
        }

        if (pathAndQuery.Length > maxLength)
        {
            error = "路径过长";
            return false;
        }

        if (pathAndQuery.Contains("..", StringComparison.Ordinal) || pathAndQuery.Contains('\\'))
        {
            error = "路径非法";
            return false;
        }

        if (!pathAndQuery.StartsWith("/ISAPI/", StringComparison.OrdinalIgnoreCase))
        {
            error = "仅允许以 /ISAPI/ 开头的设备接口路径";
            return false;
        }

        var trimmed = pathAndQuery.AsSpan();
        var q = trimmed.IndexOf('?');
        if (q >= 0)
        {
            trimmed = trimmed[..q];
        }

        var parts = trimmed.Trim('/').ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "路径格式不正确";
            return false;
        }

        if (!parts[0].Equals("ISAPI", StringComparison.OrdinalIgnoreCase))
        {
            error = "路径必须以 /ISAPI/ 开头";
            return false;
        }

        var second = parts[1];
        if (AllowedSecondSegments.All(s => !s.Equals(second, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"不支持的 ISAPI 模块「{second}」，请对照官方 Demo 或 demo-catalog";
            return false;
        }

        return true;
    }
}
