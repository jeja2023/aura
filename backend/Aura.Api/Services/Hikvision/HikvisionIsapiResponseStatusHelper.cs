/* 文件：海康 ResponseStatus 解析（HikvisionIsapiResponseStatusHelper.cs） | File: Hikvision ResponseStatus helper */
using System.Text.Json;
using System.Xml;

namespace Aura.Api.Services.Hikvision;

/// <summary>对齐 CommonBase.ResponseStatus，便于网关返回体人工排查。</summary>
internal static class HikvisionIsapiResponseStatusHelper
{
    public static string Analyze(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (raw.Contains("</ResponseStatus>", StringComparison.Ordinal))
        {
            return TryXml(raw, out var s) ? s : raw;
        }

        if (raw.Contains("\"statusString\"", StringComparison.Ordinal))
        {
            return TryJson(raw, out var s) ? s : raw;
        }

        return raw;
    }

    private static bool TryXml(string raw, out string summary)
    {
        summary = string.Empty;
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(raw);
            if (xml.DocumentElement?.Name != "ResponseStatus")
            {
                return false;
            }

            string? statusString = null;
            string? subStatusCode = null;
            string? statusCode = null;
            foreach (XmlNode node in xml.DocumentElement.ChildNodes)
            {
                if (node.Name == "statusString")
                {
                    statusString = node.InnerText;
                }
                else if (node.Name == "subStatusCode")
                {
                    subStatusCode = node.InnerText;
                }
                else if (node.Name == "statusCode")
                {
                    statusCode = node.InnerText;
                }
            }

            summary = $"statusCode={statusCode}, statusString={statusString}, subStatusCode={subStatusCode}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryJson(string raw, out string summary)
    {
        summary = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var ss = root.TryGetProperty("statusString", out var p) ? p.GetString() : null;
            var sc = root.TryGetProperty("subStatusCode", out var p2) ? p2.GetString() : null;
            summary = $"statusString={ss}, subStatusCode={sc}";
            return true;
        }
        catch
        {
            return false;
        }
    }
}
