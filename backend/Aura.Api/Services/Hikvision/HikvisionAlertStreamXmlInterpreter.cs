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
