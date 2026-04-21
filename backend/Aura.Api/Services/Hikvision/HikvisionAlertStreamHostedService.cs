/* 文件：海康告警长连接后台服务（HikvisionAlertStreamHostedService.cs） | File: Hikvision alert stream hosted service */
using System.Collections.Concurrent;
using System.Text;
using Aura.Api.Data;
using Aura.Api.Capture;
using Aura.Api.Serialization;
using Aura.Api.Ops;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Security.Cryptography;

namespace Aura.Api.Services.Hikvision;

/// <summary>
/// 在独立后台任务中维持 NVR <c>alertStream</c>，与 Kestrel 同步 ISAPI 调用解耦；可选经 SignalR 推送摘要。
/// </summary>
internal sealed class HikvisionAlertStreamHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<HikvisionIsapiOptions> _optionsMonitor;
    private readonly HikvisionAlertStreamRegistry _registry;
    private readonly ILogger<HikvisionAlertStreamHostedService> _logger;

    public HikvisionAlertStreamHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<HikvisionIsapiOptions> optionsMonitor,
        HikvisionAlertStreamRegistry registry,
        ILogger<HikvisionAlertStreamHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runners = new ConcurrentDictionary<long, CancellationTokenSource>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var opts = _optionsMonitor.CurrentValue;
                var stream = opts.AlertStream;
                if (!stream.Enabled)
                {
                    foreach (var kv in runners)
                    {
                        kv.Value.Cancel();
                        runners.TryRemove(kv.Key, out _);
                    }

                    _registry.Clear();

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(opts.DefaultUserName) || string.IsNullOrWhiteSpace(opts.DefaultPassword))
                {
                    _logger.LogWarning("海康告警长连接已启用但缺少 Hikvision:Isapi 默认凭据，已跳过。请在配置或环境变量中注入账号密码。");
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using (var scope = _scopeFactory.CreateAsyncScope())
                {
                    var devicesRepository = scope.ServiceProvider.GetRequiredService<DeviceRepository>();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var store = scope.ServiceProvider.GetRequiredService<AppStore>();
                    var devices = await ResolveTargetDevicesAsync(devicesRepository, cfg, store, stream, stoppingToken).ConfigureAwait(false);
                    var want = new HashSet<long>(devices.Select(d => d.DeviceId));

                    foreach (var id in runners.Keys.ToArray())
                    {
                        if (want.Contains(id))
                        {
                            continue;
                        }

                        if (runners.TryRemove(id, out var cts))
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                    }

                    foreach (var d in devices)
                    {
                        if (runners.ContainsKey(d.DeviceId))
                        {
                            continue;
                        }

                        var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        if (!runners.TryAdd(d.DeviceId, linked))
                        {
                            linked.Dispose();
                            continue;
                        }

                        var deviceId = d.DeviceId;
                        _ = Task.Run(() => RunDeviceLoopAsync(deviceId, linked.Token), CancellationToken.None);
                    }
                }

                var refresh = Math.Clamp(stream.SupervisorRefreshSeconds, 5, 300);
                await Task.Delay(TimeSpan.FromSeconds(refresh), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var kv in runners)
            {
                kv.Value.Cancel();
                kv.Value.Dispose();
            }

            runners.Clear();
        }
    }

    private async Task RunDeviceLoopAsync(long deviceId, CancellationToken deviceStoppingToken)
    {
        try
        {
            while (!deviceStoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var opts = _optionsMonitor.CurrentValue;
                    var stream = opts.AlertStream;
                    var client = scope.ServiceProvider.GetRequiredService<HikvisionIsapiClient>();
                    var devicesRepository = scope.ServiceProvider.GetRequiredService<DeviceRepository>();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var store = scope.ServiceProvider.GetRequiredService<AppStore>();
                    var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchService>();
                    var captureProcessingService = scope.ServiceProvider.GetRequiredService<CaptureProcessingService>();

                    var endpoint = await ResolveDeviceEndpointAsync(devicesRepository, cfg, store, deviceId, deviceStoppingToken).ConfigureAwait(false);
                    if (endpoint is null)
                    {
                        _registry.SetReconnecting(deviceId, "未解析到设备端点（库表或内存回退中无此设备）");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(stream.ReconnectSeconds, 1, 600)), deviceStoppingToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var baseUri = HikvisionIsapiBaseUri.Build(endpoint.Value.Ip, endpoint.Value.Port, opts);
                    var path = string.IsNullOrWhiteSpace(stream.PathAndQuery)
                        ? "/ISAPI/Event/notification/alertStream"
                        : stream.PathAndQuery.Trim();
                    var maxBuf = Math.Clamp(stream.MaxBufferBytes, 64 * 1024, 128 * 1024 * 1024);
                    var previewMax = Math.Clamp(stream.XmlPreviewMaxChars, 256, 100_000);

                    _registry.SetConnecting(deviceId);

                    await client.RunAlertStreamAsync(
                        baseUri,
                        path,
                        opts.DefaultUserName,
                        opts.DefaultPassword,
                        opts.SkipSslCertificateValidation,
                        maxBuf,
                        async (contentTypeLine, body) =>
                        {
                            await HandlePartAsync(
                                    deviceId,
                                    contentTypeLine,
                                    body,
                                    stream,
                                    dispatch,
                                    captureProcessingService,
                                    previewMax,
                                    deviceStoppingToken)
                                .ConfigureAwait(false);
                        },
                        _logger,
                        deviceStoppingToken,
                        onStreamEstablished: () => _registry.SetStreaming(deviceId)).ConfigureAwait(false);

                    _registry.SetReconnecting(deviceId, "本轮长读已结束（对端关闭、HTTP 失败或解析失败等），将按间隔重连");
                }
                catch (OperationCanceledException) when (deviceStoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _registry.SetError(deviceId, ex.Message);
                    _logger.LogWarning(ex, "海康告警长连接任务异常，将重连。deviceId={DeviceId}", deviceId);
                }

                try
                {
                    var reconnect = Math.Clamp(_optionsMonitor.CurrentValue.AlertStream.ReconnectSeconds, 1, 600);
                    await Task.Delay(TimeSpan.FromSeconds(reconnect), deviceStoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _registry.TryRemove(deviceId);
        }
    }

    private async Task HandlePartAsync(
        long deviceId,
        string contentTypeLine,
        byte[] body,
        HikvisionAlertStreamOptions stream,
        EventDispatchService dispatch,
        CaptureProcessingService captureProcessingService,
        int previewMax,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ct = contentTypeLine.ToLowerInvariant();
        if (ct.Contains("xml", StringComparison.Ordinal) || ct.Contains("text/xml", StringComparison.Ordinal))
        {
            if (!HikvisionAlertStreamXmlInterpreter.TrySummarize(body, out var root, out var eventType, out var eventState))
            {
                HikvisionIsapiMetrics.RecordAlertStreamPart("xml_parse_error");
                return;
            }

            if (string.Equals(root, "SubscribeEventResponse", StringComparison.Ordinal))
            {
                HikvisionIsapiMetrics.RecordAlertStreamPart("subscribe_ack");
                _registry.TouchEvent(deviceId);
                if (stream.PushSignalR)
                {
                    var preview = Encoding.UTF8.GetString(body);
                    preview = HikvisionIsapiLogFormatting.TruncateForLog(preview, previewMax);
                    await dispatch.BroadcastRoleEventAsync(
                        "hikvision.alertStream",
                        new
                        {
                            kind = "subscribe",
                            deviceId,
                            root,
                            xmlPreview = preview,
                            receivedAt = DateTimeOffset.Now
                        }).ConfigureAwait(false);
                }

                return;
            }

            if (string.Equals(eventState, "inactive", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            HikvisionIsapiMetrics.RecordAlertStreamPart("event_xml");
            _registry.TouchEvent(deviceId);
            var xmlPreview = Encoding.UTF8.GetString(body);
            xmlPreview = HikvisionIsapiLogFormatting.TruncateForLog(xmlPreview, previewMax);
            var channelNo = HikvisionAlertStreamXmlInterpreter.TryExtractChannelNo(body);
            var eventTime = HikvisionAlertStreamXmlInterpreter.TryExtractEventTime(body);
            var receivedAt = DateTimeOffset.UtcNow;
            // 若 XML 自带事件时间可解析，则优先保存为“事件发生时间”，后续图片入库用它作为 captureTime 的优先来源
            _registry.SetLastEvent(deviceId, new HikvisionAlertStreamEventSnap(root, eventType, eventState, channelNo, xmlPreview, eventTime ?? receivedAt));
            if (!stream.PushSignalR)
            {
                return;
            }

            await dispatch.BroadcastRoleEventAsync(
                "hikvision.alertStream",
                new
                {
                    kind = "event",
                    deviceId,
                    root,
                    eventType,
                    eventState,
                    channelNo,
                    eventTime,
                    xmlPreview,
                    receivedAt = receivedAt
                }).ConfigureAwait(false);
            return;
        }

        if (ct.Contains("json", StringComparison.Ordinal))
        {
            HikvisionIsapiMetrics.RecordAlertStreamPart("json");
            if (stream.LogHeartbeats)
            {
                var preview = HikvisionIsapiLogFormatting.TruncateForLog(Encoding.UTF8.GetString(body), previewMax);
                _logger.LogInformation("海康告警流 JSON 部件。deviceId={DeviceId}, 预览={Preview}", deviceId, preview);
            }
            else
            {
                _logger.LogDebug("海康告警流 JSON 部件已忽略（仅调试级）。deviceId={DeviceId}", deviceId);
            }

            return;
        }

        if (ct.Contains("image", StringComparison.Ordinal))
        {
            HikvisionIsapiMetrics.RecordAlertStreamPart("image");
            if (!stream.IngestCaptureEnabled)
            {
                return;
            }

            var maxImageBytes = Math.Clamp(stream.MaxImageBytes, 1024, 200 * 1024 * 1024);
            if (body.Length <= 0 || body.Length > maxImageBytes)
            {
                HikvisionIsapiMetrics.RecordAlertStreamPart("image_drop_oversize");
                _logger.LogWarning(
                    "海康告警流图片部件超出上限已丢弃。deviceId={DeviceId}, length={Length}, max={Max}",
                    deviceId,
                    body.Length,
                    maxImageBytes);
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;

            var recentTtlSeconds = Math.Clamp(stream.XmlRecentCacheTtlSeconds, 1, 600);
            var recentCacheSize = Math.Clamp(stream.XmlRecentCacheSize, 1, 2048);

            HikvisionAlertStreamEventSnap? picked = _registry.TryFindRecentEvent(
                deviceId,
                nowUtc,
                recentTtlSeconds,
                recentCacheSize,
                requireChannelNo: true);

            // 极端乱序：image 可能先于 XML 到达，短暂等待“最近事件”以回填
            var waitMs = Math.Clamp(stream.ImageWaitForRecentXmlMs, 0, 5000);
            if (picked is null && waitMs > 0)
            {
                var step = waitMs <= 200 ? 50 : 100;
                var deadline = nowUtc.AddMilliseconds(waitMs);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromMilliseconds(step), cancellationToken).ConfigureAwait(false);
                    picked = _registry.TryFindRecentEvent(
                        deviceId,
                        DateTimeOffset.UtcNow,
                        recentTtlSeconds,
                        recentCacheSize,
                        requireChannelNo: true);
                    if (picked is not null)
                    {
                        HikvisionIsapiMetrics.RecordAlertStreamPart("image_wait_recent_xml_hit");
                        break;
                    }
                }

                if (picked is null)
                {
                    HikvisionIsapiMetrics.RecordAlertStreamPart("image_wait_recent_xml_miss");
                }
            }

            // 若仍未找到带通道号的 XML，则退化为最近一条事件（仅用于补全元数据/时间），通道号继续走回退策略
            picked ??= _registry.TryFindRecentEvent(
                deviceId,
                DateTimeOffset.UtcNow,
                recentTtlSeconds,
                recentCacheSize,
                requireChannelNo: false);

            var channelNo = picked?.ChannelNo ?? 0;

            if (channelNo <= 0 && stream.AllowCameraChannelFallback)
            {
                var cameras = await ResolveCamerasByDeviceAsync(deviceId).ConfigureAwait(false);
                channelNo = ChooseFallbackChannelNo(cameras, stream.CameraChannelFallbackStrategy);
            }

            if (channelNo <= 0)
            {
                HikvisionIsapiMetrics.RecordAlertStreamPart("image_drop_no_channel");
                _logger.LogWarning("海康告警流图片部件缺少通道号，已跳过写入抓拍闭环。deviceId={DeviceId}", deviceId);
                return;
            }

            var hash = ComputeSha256Hex(body);
            if (_registry.ShouldDropDuplicateCapture(deviceId, channelNo, hash, nowUtc, stream.DedupWindowSeconds))
            {
                HikvisionIsapiMetrics.RecordAlertStreamPart("image_drop_duplicate");
                return;
            }

            var captureTime = picked?.ReceivedAt ?? nowUtc;
            var meta = JsonSerializer.Serialize(new
            {
                source = "hikvision.alertStream",
                deviceId,
                channelNo,
                contentType = contentTypeLine,
                imageBytes = body.Length,
                imageSha256 = hash,
                eventRoot = picked?.Root,
                eventType = picked?.EventType,
                eventState = picked?.EventState,
                eventReceivedAt = picked?.ReceivedAt,
                xmlPreview = picked?.XmlPreview ?? "",
                receivedAt = nowUtc
            }, AuraJsonSerializerOptions.Default);

            var payload = new CapturePayload
            {
                DeviceId = deviceId,
                ChannelNo = channelNo,
                CaptureTime = captureTime,
                ImageBase64 = Convert.ToBase64String(body),
                MetadataJson = meta
            };

            // 直接复用现有抓拍处理闭环（入库→AI→向量→告警→重试→事件推送）
            await captureProcessingService.ProcessAsync(payload, "海康告警流抓拍").ConfigureAwait(false);
        }
        else
        {
            HikvisionIsapiMetrics.RecordAlertStreamPart("other");
        }
    }

    private async Task<List<DbCamera>> ResolveCamerasByDeviceAsync(long deviceId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var campusResourcesRepository = scope.ServiceProvider.GetRequiredService<CampusResourceRepository>();
        return await campusResourcesRepository.GetCamerasByDeviceIdAsync(deviceId).ConfigureAwait(false);
    }

    private static int ChooseFallbackChannelNo(List<DbCamera> cameras, string? strategy)
    {
        if (cameras.Count == 0) return 0;
        var s = (strategy ?? "first").Trim().ToLowerInvariant();
        if (s == "latest")
        {
            // GetCamerasByDeviceIdAsync 已按 camera_id DESC 排序
            return cameras.FirstOrDefault()?.ChannelNo ?? 0;
        }
        // first: 选择最小有效通道号
        var min = cameras.Where(c => c.ChannelNo > 0).Select(c => c.ChannelNo).DefaultIfEmpty(0).Min();
        return min;
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool IsHikvisionIsapiDevice(DbDevice d)
    {
        var brand = d.Brand ?? "";
        var proto = d.Protocol ?? "";
        if (!proto.Contains("ISAPI", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return brand.Contains("海康", StringComparison.OrdinalIgnoreCase)
            || brand.Contains("hik", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<DbDevice>> ResolveTargetDevicesAsync(
        DeviceRepository devicesRepository,
        IConfiguration configuration,
        AppStore store,
        HikvisionAlertStreamOptions stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await devicesRepository.GetDevicesAsync().ConfigureAwait(false);
        var filtered = rows.Where(IsHikvisionIsapiDevice).ToList();
        if (stream.DeviceIds is { Length: > 0 })
        {
            var set = new HashSet<long>(stream.DeviceIds);
            filtered = filtered.Where(d => set.Contains(d.DeviceId)).ToList();
        }

        if (configuration.GetValue("Aura:AllowInMemoryDataFallback", false))
        {
            foreach (var mem in store.Devices)
            {
                if (!IsHikvisionIsapiDevice(
                        new DbDevice(
                            mem.DeviceId,
                            mem.Name,
                            mem.Ip,
                            mem.Port,
                            mem.Brand,
                            mem.Protocol,
                            mem.Status,
                            mem.CreatedAt.UtcDateTime)))
                {
                    continue;
                }

                if (stream.DeviceIds is { Length: > 0 } && !stream.DeviceIds.Contains(mem.DeviceId))
                {
                    continue;
                }

                if (filtered.All(x => x.DeviceId != mem.DeviceId))
                {
                    filtered.Add(
                        new DbDevice(
                            mem.DeviceId,
                            mem.Name,
                            mem.Ip,
                            mem.Port,
                            mem.Brand,
                            mem.Protocol,
                            mem.Status,
                            mem.CreatedAt.UtcDateTime));
                }
            }
        }

        return filtered;
    }

    private static async Task<(long DeviceId, string Name, string Ip, int Port)?> ResolveDeviceEndpointAsync(
        DeviceRepository devicesRepository,
        IConfiguration configuration,
        AppStore store,
        long deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = await devicesRepository.GetDeviceByIdAsync(deviceId).ConfigureAwait(false);
        if (row is not null)
        {
            return (row.DeviceId, row.Name, row.Ip, row.Port);
        }

        if (configuration.GetValue("Aura:AllowInMemoryDataFallback", false))
        {
            var mem = store.Devices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (mem is not null)
            {
                return (mem.DeviceId, mem.Name, mem.Ip, mem.Port);
            }
        }

        return null;
    }
}
