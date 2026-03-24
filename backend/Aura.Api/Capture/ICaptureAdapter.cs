/* 文件：抓拍适配器接口（ICaptureAdapter.cs） | File: Capture Adapter Interface */
using System.Text.Json;

namespace Aura.Api.Capture;

public interface ICaptureAdapter
{
    string Name { get; }

    CapturePayload Normalize(JsonElement rawPayload);
}
