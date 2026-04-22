using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;

internal sealed class RetryProcessingService
{
    private readonly AppStore _store;
    private readonly CaptureRepository _captureRepository;
    private readonly AuditRepository _auditRepository;
    private readonly RedisCacheService _cache;
    private readonly RetryQueueService _retryQueue;
    private readonly AiClient _aiClient;

    public RetryProcessingService(
        AppStore store,
        CaptureRepository captureRepository,
        AuditRepository auditRepository,
        RedisCacheService cache,
        RetryQueueService retryQueue,
        AiClient aiClient)
    {
        _store = store;
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
                return AuraApiResults.TooManyRequests("重试任务处理中，请稍后再试（可能已有其他实例或会话正在执行）", 42902);
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
                    AiUpsertResult? upsert = null;
                    string? vectorId = null;
                    if (ai.Feature.Count > 0)
                    {
                        vectorId = $"C_{task.CaptureId}";
                        _ = await _captureRepository.UpdateCaptureFeatureIdAsync(task.CaptureId, vectorId);
                        upsert = await _aiClient.UpsertAsync(vectorId, ai.Feature);
                        if (!upsert.Success)
                        {
                            failed++;
                            var retryQueued = false;
                            if (task.RetryCount < 3)
                            {
                                await _retryQueue.EnqueueAsync(task with { RetryCount = task.RetryCount + 1 });
                                retryQueued = true;
                            }
                            else
                            {
                                TryDeleteFile(task.ImagePath);
                            }

                            var vectorFailedMetadata = AiMetadataComposer.Compose(
                                task.MetadataJson,
                                ai,
                                vectorId: vectorId,
                                vectorUpsertResult: upsert,
                                retryQueued: retryQueued,
                                retryReason: retryQueued ? "重试补偿中" : "向量补偿失败且已达到最大重试次数");
                            await UpdateCaptureMetadataStateAsync(task.CaptureId, vectorFailedMetadata);
                            await _auditRepository.InsertOperationAsync("重试任务", "AI向量补偿失败", $"captureId={task.CaptureId}, 设备={task.DeviceId}, 通道={task.ChannelNo}, 原因={upsert.Message}");
                            continue;
                        }
                    }

                    var newMetadata = AiMetadataComposer.Compose(
                        task.MetadataJson,
                        ai,
                        vectorId: vectorId,
                        vectorUpsertResult: upsert,
                        retryQueued: false,
                        retryReason: upsert is null ? null : "重试补偿已完成");
                    await UpdateCaptureMetadataStateAsync(task.CaptureId, newMetadata);
                    success++;
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

                var extractFailedMetadata = AiMetadataComposer.Compose(
                    task.MetadataJson,
                    ai,
                    vectorId: null,
                    vectorUpsertResult: null,
                    retryQueued: task.RetryCount < 3,
                    retryReason: task.RetryCount < 3 ? "AI 提取失败，继续重试中" : "AI 提取失败且已达到最大重试次数");
                await UpdateCaptureMetadataStateAsync(task.CaptureId, extractFailedMetadata);
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

    private async Task UpdateCaptureMetadataStateAsync(long captureId, string metadataJson)
    {
        var updated = await _captureRepository.UpdateCaptureMetadataAsync(captureId, metadataJson);
        if (updated)
        {
            return;
        }

        var idx = _store.Captures.FindIndex(x => x.CaptureId == captureId);
        if (idx >= 0)
        {
            _store.Captures[idx] = _store.Captures[idx] with { MetadataJson = metadataJson };
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
