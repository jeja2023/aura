/* 文件：海康 ISAPI 原始载荷（HikvisionIsapiHttpPayload.cs） | File: Hikvision ISAPI raw payload */
namespace Aura.Api.Services.Hikvision;

internal sealed class HikvisionIsapiHttpPayload
{
    public bool IsBinary { get; init; }
    public string? TextBody { get; init; }
    public byte[]? BinaryBody { get; init; }
    public string? ContentType { get; init; }
}
