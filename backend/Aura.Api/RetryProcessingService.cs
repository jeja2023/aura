using System.Text.Json;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Aura.Api.Serialization;

internal sealed class RetryProcessingService
{
    private readonly CaptureRepository _captureRepository;
    private readonly AuditRepository _auditRepository;
    private readonly RedisCacheService _cache;
    private readonly RetryQueueService _retryQueue;
    private readonly AiClient _aiClient;

    public RetryProcessingService(
        CaptureRepository captureRepository,
        AuditRepository auditRepository,
        RedisCacheService cache,
        RetryQueueService retryQueue,
        AiClient aiClient)
    {
        _captureRepository = captureRepository;
        _auditRepository = auditRepository;
        _cache = cache;
        _retryQueue = retryQueue;
        _aiClient = aiClient;
    }

    public async Task<IResult> GetStatusAsync()
    {
        var count = await _retryQueue.LengthAsync();
        return Results.Ok(new { code = 0, msg = "查询成功", data = new { enabled = _retryQueue.Enabled, pending = count } });
    }

    public async Task<IResult> ProcessAsync(RetryProcessReq req)
    {
        const string processLockKey = "aura:lock:retry-process";
        const int processLockMinutes = 30;
        string? lockToken = null;
        if (_cache.Enabled)
        {
            lockToken = await _cache.TryAcquireLockAsync(processLockKey, TimeSpan.FromMinutes(processLockMinutes));
            if (lockToken is null)
            {
                return AuraApiResults.TooManyRequests("重试任务处理正在进行中，请稍后再试（其他实例或会话可能正在执行）", 42902);
            }
        }

        var take = req.Take <= 0 ? 10 : Math.Min(req.Take, 100);
        var success = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < take; i++)
            {
                var task = await _retryQueue.DequeueAsync();
                if (task is null)
                {
                    break;
                }

                AiExtractResult ai;
                if (!string.IsNullOrWhiteSpace(task.ImagePath))
                {
                    ai = await _aiClient.ExtractByPathAsync(task.ImagePath, task.MetadataJson);
                    if (!ai.Success && !string.IsNullOrWhiteSpace(task.ImageBase64))
                    {
                        ai = await _aiClient.ExtractAsync(task.ImageBase64, task.MetadataJson);
                    }
                }
                else
                {
                    ai = await _aiClient.ExtractAsync(task.ImageBase64 ?? string.Empty, task.MetadataJson);
                }

                if (ai.Success)
                {
                    success++;
                    var newMetadata = AttachAiResult(task.MetadataJson, ai);
                    _ = await _captureRepository.UpdateCaptureMetadataAsync(task.CaptureId, newMetadata);
                    if (ai.Feature.Count > 0)
                    {
                        var vectorId = $"C_{task.CaptureId}";
                        await _aiClient.UpsertAsync(vectorId, ai.Feature);
                    }

                    await _auditRepository.InsertOperationAsync("重试任务", "AI重试成功", $"captureId={task.CaptureId}, 设备={task.DeviceId}, 通道={task.ChannelNo}");
                    TryDeleteFile(task.ImagePath);
                    continue;
                }

                failed++;
                if (task.RetryCount < 3)
                {
                    await _retryQueue.EnqueueAsync(task with { RetryCount = task.RetryCount + 1 });
                }
                else
                {
                    TryDeleteFile(task.ImagePath);
                }

                await _auditRepository.InsertOperationAsync("重试任务", "AI重试失败", $"设备={task.DeviceId}, 通道={task.ChannelNo}, 原因={ai.Message}");
            }
        }
        finally
        {
            if (lockToken is not null)
            {
                await _cache.ReleaseLockAsync(processLockKey, lockToken);
            }
        }

        return Results.Ok(new { code = 0, msg = "处理完成", data = new { take, success, failed } });
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
            return JsonSerializer.Serialize(map, AuraJsonSerializerOptions.Default);
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                raw = metadataJson,
                ai_success = aiResult.Success,
                ai_dim = aiResult.Dim,
                ai_msg = aiResult.Message
            }, AuraJsonSerializerOptions.Default);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}
