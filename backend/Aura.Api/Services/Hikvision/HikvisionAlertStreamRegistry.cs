/* 文件：海康告警长连接状态注册表（HikvisionAlertStreamRegistry.cs） | File: Hikvision alert stream registry */
using System.Collections.Concurrent;

namespace Aura.Api.Services.Hikvision;

/// <summary>进程内可观测状态：供 <c>GET /api/device/hikvision/alert-stream-status</c> 与运维排查，非持久化。</summary>
internal sealed class HikvisionAlertStreamRegistry
{
    private readonly ConcurrentDictionary<long, HikvisionAlertStreamDeviceSnap> _devices = new();

    public void SetConnecting(long deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        _devices[deviceId] = new HikvisionAlertStreamDeviceSnap("connecting", now, null, null, "正在建立 HTTP 长连接");
    }

    public void SetStreaming(long deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        _devices.AddOrUpdate(
            deviceId,
            _ => new HikvisionAlertStreamDeviceSnap("streaming", now, now, null, "已收到响应并进入读循环"),
            (_, prev) => prev with
            {
                Phase = "streaming",
                UpdatedAt = now,
                StreamingSince = prev.StreamingSince ?? now,
                Detail = "读循环中"
            });
    }

    public void TouchEvent(long deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        _devices.AddOrUpdate(
            deviceId,
            _ => new HikvisionAlertStreamDeviceSnap("streaming", now, now, now, "已解析部件"),
            (_, prev) => prev with { UpdatedAt = now, LastEventAt = now });
    }

    public void SetReconnecting(long deviceId, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var detail = HikvisionIsapiLogFormatting.TruncateForLog(reason, 500);
        _devices.AddOrUpdate(
            deviceId,
            _ => new HikvisionAlertStreamDeviceSnap("reconnecting", now, null, null, detail),
            (_, prev) => prev with { Phase = "reconnecting", UpdatedAt = now, Detail = detail });
    }

    public void SetError(long deviceId, string message)
    {
        var now = DateTimeOffset.UtcNow;
        var detail = HikvisionIsapiLogFormatting.TruncateForLog(message, 500);
        _devices.AddOrUpdate(
            deviceId,
            _ => new HikvisionAlertStreamDeviceSnap("error", now, null, null, detail),
            (_, prev) => prev with { Phase = "error", UpdatedAt = now, Detail = detail });
    }

    public bool TryRemove(long deviceId) => _devices.TryRemove(deviceId, out _);

    public void Clear() => _devices.Clear();

    public object BuildSnapshot(HikvisionIsapiOptions opt)
    {
        var alert = opt.AlertStream;
        var list = _devices
            .OrderBy(static kv => kv.Key)
            .Select(kv => new
            {
                deviceId = kv.Key,
                kv.Value.Phase,
                kv.Value.UpdatedAt,
                kv.Value.StreamingSince,
                kv.Value.LastEventAt,
                detail = kv.Value.Detail
            })
            .ToArray();

        return new
        {
            alertStreamEnabled = alert.Enabled,
            pathAndQuery = alert.PathAndQuery,
            pushSignalR = alert.PushSignalR,
            supervisorRefreshSeconds = alert.SupervisorRefreshSeconds,
            reconnectSeconds = alert.ReconnectSeconds,
            deviceIdsFilter = alert.DeviceIds,
            trackedDevices = list
        };
    }
}

internal sealed record HikvisionAlertStreamDeviceSnap(
    string Phase,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StreamingSince,
    DateTimeOffset? LastEventAt,
    string? Detail);
