/* 文件：海康 ISAPI 基址构造（HikvisionIsapiBaseUri.cs） | File: Hikvision ISAPI base URI builder */
namespace Aura.Api.Services.Hikvision;

internal static class HikvisionIsapiBaseUri
{
    public static Uri Build(string ip, int port, HikvisionIsapiOptions opt)
    {
        var scheme = opt.UseHttps ? "https" : "http";
        return new Uri($"{scheme}://{ip}:{port}/");
    }
}
