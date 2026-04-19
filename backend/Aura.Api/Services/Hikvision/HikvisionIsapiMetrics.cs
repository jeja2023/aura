/* 文件：海康 ISAPI Prometheus 指标（HikvisionIsapiMetrics.cs） | File: Hikvision ISAPI Prometheus metrics */
using Prometheus;

namespace Aura.Api.Services.Hikvision;

/// <summary>海康 ISAPI 出站与网关调用的可观测性指标（供 Prometheus 抓取）。</summary>
internal static class HikvisionIsapiMetrics
{
    private static readonly Histogram OutboundDuration = Metrics.CreateHistogram(
        "aura_hikvision_isapi_outbound_duration_seconds",
        "海康设备 ISAPI 出站 HTTP 调用耗时（秒）",
        new HistogramConfiguration
        {
            LabelNames = new[] { "operation", "result" },
            Buckets = Histogram.ExponentialBuckets(0.025, 2, 14)
        });

    private static readonly Counter GatewayTotal = Metrics.CreateCounter(
        "aura_hikvision_gateway_invocations_total",
        "海康 ISAPI 通用网关调用次数",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    private static readonly Counter DeviceApiTotal = Metrics.CreateCounter(
        "aura_hikvision_device_api_calls_total",
        "海康封装设备接口出站结果（按操作名聚合）",
        new CounterConfiguration { LabelNames = new[] { "operation", "result" } });

    public static void ObserveOutbound(string operation, bool success, double elapsedSeconds)
    {
        var op = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation.Trim();
        var result = success ? "success" : "failure";
        OutboundDuration.WithLabels(op, result).Observe(Math.Max(0, elapsedSeconds));
    }

    public static void GatewayInvocation(bool success)
    {
        GatewayTotal.WithLabels(success ? "success" : "failure").Inc();
    }

    /// <summary>封装接口在已发起设备 HTTP 后的结果（成功=设备返回成功解析，失败=502 或空数据）。</summary>
    public static void RecordDeviceApi(string operation, bool upstreamSuccess)
    {
        var op = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation.Trim();
        DeviceApiTotal.WithLabels(op, upstreamSuccess ? "success" : "failure").Inc();
    }
}
