using System.Text.Json;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Capture;
using Aura.Api.Data;
using Aura.Api.Models;
using Aura.Api.Ops;
using Microsoft.AspNetCore.Http;

internal sealed class CaptureProcessingService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;
    private readonly RetryQueueService _retryQueue;
    private readonly AiClient _aiClient;
    private readonly EventDispatchService _eventDispatchService;
    private readonly string _storageRoot;
    private readonly string _captureRetryImageFolder;
    private readonly bool _captureRetryPreferInlineBase64;
    private readonly bool _captureRetryAllowInlineFallback;
    private readonly bool _saveCaptureImageOnSuccess;

    public CaptureProcessingService(
        AppStore store,
        PgSqlStore db,
        RetryQueueService retryQueue,
        AiClient aiClient,
        EventDispatchService eventDispatchService,
        string storageRoot,
        string captureRetryImageFolder,
        bool captureRetryPreferInlineBase64,
        bool captureRetryAllowInlineFallback,
        bool saveCaptureImageOnSuccess)
    {
        _store = store;
        _db = db;
        _retryQueue = retryQueue;
        _aiClient = aiClient;
        _eventDispatchService = eventDispatchService;
        _storageRoot = storageRoot;
        _captureRetryImageFolder = captureRetryImageFolder;
        _captureRetryPreferInlineBase64 = captureRetryPreferInlineBase64;
        _captureRetryAllowInlineFallback = captureRetryAllowInlineFallback;
        _saveCaptureImageOnSuccess = saveCaptureImageOnSuccess;
    }

    public async Task<IResult> ProcessAsync(CapturePayload normalized, string source)
    {
        string? captureImagePathForDb = null;
        string? retryImagePath = await SaveRetryImageAsync(normalized.ImageBase64);
        string? retryImageBase64ForQueue = null;
        var shouldEnqueueRetry = false;

        var aiResult = !string.IsNullOrWhiteSpace(retryImagePath)
            ? await _aiClient.ExtractByPathAsync(retryImagePath, normalized.MetadataJson)
            : await _aiClient.ExtractAsync(normalized.ImageBase64, normalized.MetadataJson);

        if (!aiResult.Success)
        {
            captureImagePathForDb = ToPublicStorageUrl(_storageRoot, retryImagePath);
            if (!string.IsNullOrWhiteSpace(retryImagePath))
            {
                shouldEnqueueRetry = true;
                retryImageBase64ForQueue = null;
            }
            else if (_captureRetryAllowInlineFallback)
            {
                shouldEnqueueRetry = true;
                retryImageBase64ForQueue = normalized.ImageBase64;
            }
        }
        else if (_saveCaptureImageOnSuccess)
        {
            captureImagePathForDb = ToPublicStorageUrl(_storageRoot, retryImagePath)
                                    ?? await SaveCaptureArchiveImageAsync(normalized.DeviceId, normalized.CaptureTime, normalized.ImageBase64);
        }
        else if (!string.IsNullOrWhiteSpace(retryImagePath))
        {
            TryDeleteFile(retryImagePath);
        }

        var metadata = AttachAiResult(normalized.MetadataJson, aiResult);
        var record = new CaptureEntity(
            Interlocked.Increment(ref _store.CaptureSeed),
            normalized.DeviceId,
            normalized.ChannelNo,
            normalized.CaptureTime,
            metadata,
            captureImagePathForDb);
        var dbId = await _db.InsertCaptureAsync(record.DeviceId, record.ChannelNo, record.CaptureTime, record.MetadataJson, captureImagePathForDb);
        var saved = dbId.HasValue ? record with { CaptureId = dbId.Value } : record;
        if (!dbId.HasValue)
        {
            _store.Captures.Add(saved);
        }

        if (aiResult.Success && aiResult.Feature.Count > 0)
        {
            var vectorId = $"C_{saved.CaptureId}";
            await _aiClient.UpsertAsync(vectorId, aiResult.Feature);
        }

        await _db.InsertOperationAsync("采集网关", source, $"设备={normalized.DeviceId}, 通道={normalized.ChannelNo}, AI={aiResult.Message}");
        AddOperationLog("采集网关", source, $"设备={normalized.DeviceId}, 通道={normalized.ChannelNo}, AI={aiResult.Message}");
        await _eventDispatchService.BroadcastRoleEventAsync("capture.received", new { saved.CaptureId, saved.DeviceId, saved.ChannelNo, saved.CaptureTime, source });

        if (!aiResult.Success && shouldEnqueueRetry)
        {
            await _retryQueue.EnqueueAsync(new RetryTask(
                saved.CaptureId,
                normalized.DeviceId,
                normalized.ChannelNo,
                retryImagePath,
                retryImageBase64ForQueue,
                normalized.MetadataJson,
                source,
                0,
                DateTimeOffset.Now));
        }
        else if (!aiResult.Success)
        {
            await _db.InsertOperationAsync("重试任务", "AI重试入队已跳过", $"captureId={saved.CaptureId}, 原因=图片落盘失败且禁止内联Base64回退");
        }

        if (!string.IsNullOrWhiteSpace(normalized.MetadataJson) && normalized.MetadataJson.Contains("异常", StringComparison.Ordinal))
        {
            var alert = new AlertEntity(
                Interlocked.Increment(ref _store.AlertSeed),
                "异常滞留",
                $"抓拍记录{saved.CaptureId}命中异常关键词",
                DateTimeOffset.Now);
            var alertId = await _db.InsertAlertAsync(alert.AlertType, alert.Detail);
            if (!alertId.HasValue)
            {
                _store.Alerts.Add(alert);
            }
            await _eventDispatchService.NotifyAlertAsync(alert.AlertType, alert.Detail, "抓拍关键词命中");
            await _eventDispatchService.BroadcastRoleEventAsync("alert.created", new { alertType = alert.AlertType, detail = alert.Detail, at = alert.CreatedAt });
        }

        return Results.Ok(new { code = 0, msg = $"{source}接收成功", data = saved });
    }

    private void AddOperationLog(string operatorName, string action, string detail)
    {
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: operatorName,
            Action: action,
            Detail: detail,
            CreatedAt: DateTimeOffset.Now));
    }

    private async Task<string?> SaveRetryImageAsync(string imageBase64)
    {
        if (_captureRetryPreferInlineBase64)
        {
            return null;
        }

        var pure = TryExtractPureBase64(imageBase64);
        if (string.IsNullOrWhiteSpace(pure))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(pure);
        }
        catch
        {
            return null;
        }

        const long maxImageBytes = 10L * 1024 * 1024;
        if (bytes.Length <= 0 || bytes.Length > maxImageBytes)
        {
            return null;
        }

        Directory.CreateDirectory(_captureRetryImageFolder);
        var localPath = Path.Combine(_captureRetryImageFolder, $"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localPath, bytes);
        return localPath;
    }

    private async Task<string?> SaveCaptureArchiveImageAsync(long deviceId, DateTimeOffset captureTime, string imageBase64)
    {
        var pure = TryExtractPureBase64(imageBase64);
        if (string.IsNullOrWhiteSpace(pure))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(pure);
        }
        catch
        {
            return null;
        }

        const long maxImageBytes = 10L * 1024 * 1024;
        if (bytes.Length <= 0 || bytes.Length > maxImageBytes)
        {
            return null;
        }

        var folder = Path.Combine(_storageRoot, "uploads", "capture", deviceId.ToString(), captureTime.ToString("yyyyMMdd"));
        Directory.CreateDirectory(folder);
        var localPath = Path.Combine(folder, $"{captureTime:HHmmss}_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localPath, bytes);
        return ToPublicStorageUrl(_storageRoot, localPath);
    }

    private static string AttachAiResult(string metadataJson, AiExtractResult aiResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            var map = new Dictionary<string, object?>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    map[p.Name] = p.Value.ToString();
                }
            }
            map["ai_success"] = aiResult.Success;
            map["ai_dim"] = aiResult.Dim;
            map["ai_msg"] = aiResult.Message;
            return JsonSerializer.Serialize(map);
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                raw = metadataJson,
                ai_success = aiResult.Success,
                ai_dim = aiResult.Dim,
                ai_msg = aiResult.Message
            });
        }
    }

    private static string? TryExtractPureBase64(string imageBase64)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return null;
        }

        var idx = imageBase64.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return imageBase64[(idx + "base64,".Length)..];
        }

        var comma = imageBase64.IndexOf(',');
        if (imageBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            return imageBase64[(comma + 1)..];
        }

        return imageBase64;
    }

    private static string? ToPublicStorageUrl(string? storageRootPath, string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(storageRootPath))
        {
            return null;
        }

        try
        {
            var fullRoot = Path.GetFullPath(storageRootPath);
            var fullLocal = Path.GetFullPath(localPath);
            if (!fullLocal.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var rel = Path.GetRelativePath(fullRoot, fullLocal).Replace('\\', '/');
            return $"/storage/{rel}";
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}
