/* 文件：海康告警 XML 摘要（HikvisionAlertStreamXmlInterpreter.cs） | File: Hikvision alert XML summary */
using System.Text;
using System.Xml;

namespace Aura.Api.Services.Hikvision;

internal static class HikvisionAlertStreamXmlInterpreter
{
    public static bool TrySummarize(byte[] utf8Bytes, out string rootName, out string? eventType, out string? eventState)
    {
        rootName = "";
        eventType = null;
        eventState = null;
        try
        {
            var text = Encoding.UTF8.GetString(utf8Bytes);
            var doc = new XmlDocument();
            doc.LoadXml(text);
            var root = doc.DocumentElement;
            if (root is null)
            {
                return false;
            }

            rootName = root.Name;
            eventType = FindChildText(root, "eventType");
            eventState = FindChildText(root, "eventState");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int? TryExtractChannelNo(byte[] utf8Bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(utf8Bytes);
            var doc = new XmlDocument();
            doc.LoadXml(text);

            // 兼容多型号：常见节点名 channelID / channelId / channelNo / channel
            foreach (var tag in new[] { "channelID", "channelId", "channelNo", "channel", "cameraNo" })
            {
                var nodes = doc.GetElementsByTagName(tag);
                if (nodes.Count <= 0) continue;
                foreach (XmlNode n in nodes)
                {
                    var v = n.InnerText?.Trim();
                    if (int.TryParse(v, out var channel) && channel > 0 && channel <= 512)
                    {
                        return channel;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static DateTimeOffset? TryExtractEventTime(byte[] utf8Bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(utf8Bytes);
            var doc = new XmlDocument();
            doc.LoadXml(text);

            // 兼容多型号：eventTime/dateTime/Time 等常见字段；优先取第一个可解析的。
            foreach (var tag in new[] { "eventTime", "dateTime", "time", "occurTime" })
            {
                var nodes = doc.GetElementsByTagName(tag);
                if (nodes.Count <= 0) continue;
                foreach (XmlNode n in nodes)
                {
                    var v = n.InnerText?.Trim();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (DateTimeOffset.TryParse(v, out var dto))
                    {
                        return dto;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindChildText(XmlNode root, string name)
    {
        foreach (XmlNode n in root.ChildNodes)
        {
            if (n.Name == name)
            {
                return n.InnerText?.Trim();
            }
        }

        return null;
    }
}
