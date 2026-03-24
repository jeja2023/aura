/* 文件：C++ SDK抓拍适配器（CppSdkAdapter.cs） | File: C++ SDK Capture Adapter */
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aura.Api.Capture.Adapters;

public sealed class CppSdkAdapter : ICaptureAdapter
{
    public string Name => "cpp-sdk";

    public CapturePayload Normalize(JsonElement rawPayload)
    {
        var deviceId = TryGetLong(rawPayload, "deviceId", 0);
        var channelNo = (int)TryGetLong(rawPayload, "channelNo", 0);
        var imageBase64 = TryGetString(rawPayload, "imageBase64");
        var timestamp = TryGetString(rawPayload, "timestamp");
        var captureTime = DateTimeOffset.TryParse(timestamp, out var dt) ? dt : DateTimeOffset.Now;
        return new CapturePayload
        {
            DeviceId = deviceId,
            ChannelNo = channelNo,
            CaptureTime = captureTime,
            ImageBase64 = imageBase64,
            // metadata 里不应包含 imageBase64，避免重复体积传输与入库膨胀
            MetadataJson = RemoveImageBase64(rawPayload)
        };
    }

    private static string TryGetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static long TryGetLong(JsonElement root, string name, long defaultValue)
    {
        if (root.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
            {
                return n;
            }
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var s))
            {
                return s;
            }
        }
        return defaultValue;
    }

    private static string RemoveImageBase64(JsonElement rawPayload)
    {
        try
        {
            if (rawPayload.ValueKind != JsonValueKind.Object) return rawPayload.GetRawText();
            var node = JsonNode.Parse(rawPayload.GetRawText());
            if (node is JsonObject obj)
            {
                obj.Remove("imageBase64");
                obj.Remove("image_base64");
            }
            return node?.ToJsonString() ?? "{}";
        }
        catch
        {
            // 兜底：无法解析时退回原始 JSON，避免链路整体失败
            return rawPayload.GetRawText();
        }
    }
}
