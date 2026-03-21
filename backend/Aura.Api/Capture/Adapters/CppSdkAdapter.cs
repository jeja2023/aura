using System.Text.Json;

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
            MetadataJson = rawPayload.GetRawText()
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
}
