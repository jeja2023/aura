/* 文件：海康告警长连接状态注册表（HikvisionAlertStreamRegistry.cs） | File: Hikvision alert stream registry */
using System.Collections.Concurrent;
using System.Linq;

namespace Aura.Api.Services.Hikvision;

/// <summary>进程内可观测状态：供 <c>GET /api/device/hikvision/alert-stream-status</c> 与运维排查，非持久化。</summary>
internal sealed class HikvisionAlertStreamRegistry
{
    private readonly ConcurrentDictionary<long, HikvisionAlertStreamDeviceSnap> _devices = new();
    private readonly ConcurrentDictionary<long, HikvisionAlertStreamEventSnap> _lastEvents = new();
    private readonly ConcurrentDictionary<long, HikvisionAlertStreamRecentEvents> _recentEvents = new();
    private readonly ConcurrentDictionary<string, HikvisionAlertStreamDedupSnap> _dedup = new();

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

    public void SetLastEvent(long deviceId, HikvisionAlertStreamEventSnap snap)
    {
        _lastEvents[deviceId] = snap;
        _recentEvents.GetOrAdd(deviceId, static _ => new HikvisionAlertStreamRecentEvents()).Add(snap);
    }

    public HikvisionAlertStreamEventSnap? TryGetLastEvent(long deviceId)
    {
        return _lastEvents.TryGetValue(deviceId, out var v) ? v : null;
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

    public void Clear()
    {
        _devices.Clear();
        _lastEvents.Clear();
        _recentEvents.Clear();
        _dedup.Clear();
    }

    public HikvisionAlertStreamEventSnap? TryFindRecentEvent(
        long deviceId,
        DateTimeOffset nowUtc,
        int maxAgeSeconds,
        int maxCacheSize,
        bool requireChannelNo)
    {
        if (!_recentEvents.TryGetValue(deviceId, out var buf))
        {
            return null;
        }

        buf.Prune(nowUtc, maxAgeSeconds, maxCacheSize);
        return buf.TryPickBest(nowUtc, maxAgeSeconds, requireChannelNo);
    }

    public bool ShouldDropDuplicateCapture(long deviceId, int channelNo, string imageHash, DateTimeOffset now, int windowSeconds)
    {
        if (windowSeconds <= 0)
        {
            return false;
        }

        var w = Math.Clamp(windowSeconds, 1, 600);
        var key = $"{deviceId}:{channelNo}";

        if (!_dedup.TryGetValue(key, out var prev))
        {
            _dedup[key] = new HikvisionAlertStreamDedupSnap(imageHash, now);
            return false;
        }

        if ((now - prev.UpdatedAt).TotalSeconds > w * 2)
        {
            _dedup[key] = new HikvisionAlertStreamDedupSnap(imageHash, now);
            return false;
        }

        if (string.Equals(prev.ImageHash, imageHash, StringComparison.Ordinal)
            && (now - prev.UpdatedAt).TotalSeconds <= w)
        {
            return true;
        }

        _dedup[key] = new HikvisionAlertStreamDedupSnap(imageHash, now);
        return false;
    }

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
            ingestCaptureEnabled = alert.IngestCaptureEnabled,
            maxImageBytes = alert.MaxImageBytes,
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

internal sealed record HikvisionAlertStreamEventSnap(
    string Root,
    string? EventType,
    string? EventState,
    int? ChannelNo,
    string XmlPreview,
    DateTimeOffset ReceivedAt);

internal sealed record HikvisionAlertStreamDedupSnap(string ImageHash, DateTimeOffset UpdatedAt);

internal sealed class HikvisionAlertStreamRecentEvents
{
    private readonly object _gate = new();
    private readonly Queue<HikvisionAlertStreamEventSnap> _queue = new();

    public void Add(HikvisionAlertStreamEventSnap snap)
    {
        lock (_gate)
        {
            _queue.Enqueue(snap);
        }
    }

    public void Prune(DateTimeOffset nowUtc, int maxAgeSeconds, int maxCacheSize)
    {
        var age = Math.Clamp(maxAgeSeconds, 1, 600);
        var cap = Math.Clamp(maxCacheSize, 1, 2048);
        lock (_gate)
        {
            while (_queue.Count > cap)
            {
                _queue.Dequeue();
            }

            while (_queue.Count > 0)
            {
                var head = _queue.Peek();
                if ((nowUtc - head.ReceivedAt).TotalSeconds <= age)
                {
                    break;
                }
                _queue.Dequeue();
            }
        }
    }

    public HikvisionAlertStreamEventSnap? TryPickBest(DateTimeOffset nowUtc, int maxAgeSeconds, bool requireChannelNo)
    {
        var age = Math.Clamp(maxAgeSeconds, 1, 600);
        lock (_gate)
        {
            if (_queue.Count == 0) return null;

            // 策略：从新到旧找第一个仍在窗口内的事件；可选要求携带有效通道号
            // 说明：这里不按 XML 的 eventTime 做回填，以避免设备时钟漂移导致跨窗误配；以“接收时间 ReceivedAt”作为稳态基准
            HikvisionAlertStreamEventSnap? best = null;
            foreach (var e in _queue.Reverse())
            {
                if ((nowUtc - e.ReceivedAt).TotalSeconds > age)
                {
                    break;
                }

                if (requireChannelNo && (e.ChannelNo ?? 0) <= 0)
                {
                    continue;
                }

                best = e;
                break;
            }

            return best;
        }
    }
}
