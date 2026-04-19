/* 文件：海康 ISAPI 分布式追踪（HikvisionIsapiActivity.cs） | File: Hikvision ISAPI tracing activity */
using System.Diagnostics;

namespace Aura.Api.Services.Hikvision;

/// <summary>海康 ISAPI 出站调用的 <see cref="Activity"/> 源，便于与 OpenTelemetry 链路关联。</summary>
internal static class HikvisionIsapiActivity
{
    private static readonly ActivitySource Source = new("Aura.HikvisionIsapi", "1.0.0");

    public static Activity? StartOutbound(string operation, Uri baseUri, string pathAndQuery)
    {
        var name = string.IsNullOrWhiteSpace(operation) ? "hikvision.isapi" : operation.Trim();
        var activity = Source.StartActivity(name, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("server.address", baseUri.Host);
        if (!baseUri.IsDefaultPort)
        {
            activity.SetTag("server.port", baseUri.Port);
        }
        var path = pathAndQuery.Length > 512 ? pathAndQuery[..512] : pathAndQuery;
        activity.SetTag("url.path", path);
        return activity;
    }

    public static Activity? StartGateway(long deviceId, string method, string pathAndQuery)
    {
        var activity = Source.StartActivity("hikvision.gateway", ActivityKind.Client);
        activity?.SetTag("hikvision.device_id", deviceId);
        activity?.SetTag("http.request.method", method);
        var p = pathAndQuery.Length > 512 ? pathAndQuery[..512] : pathAndQuery;
        activity?.SetTag("url.path", p);
        return activity;
    }
}
