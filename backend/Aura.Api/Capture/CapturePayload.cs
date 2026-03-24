/* 文件：抓拍载荷模型（CapturePayload.cs） | File: Capture Payload Model */
namespace Aura.Api.Capture;

public sealed class CapturePayload
{
    public long DeviceId { get; set; }
    public int ChannelNo { get; set; }
    public DateTimeOffset CaptureTime { get; set; }
    public string ImageBase64 { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
}
