using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Aura.Api.Serialization;
using Aura.Api.Vector;
using Microsoft.AspNetCore.Http;

internal sealed class VectorApplicationService
{
    private const string ExtractCachePrefix = "vector:extract:v1";
    private const string SearchCachePrefix = "vector:search:v1";
    private static readonly TimeSpan ExtractCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromSeconds(20);

    private readonly AiClient _aiClient;
    private readonly LegacyVectorBridge _vectorBridge;
    private readonly CaptureRepository _captureRepository;
    private readonly RedisCacheService _cache;
    private readonly int _maxImageBase64Chars;
    private readonly int _maxMetadataJsonChars;

    public VectorApplicationService(
        AiClient aiClient,
        LegacyVectorBridge vectorBridge,
        CaptureRepository captureRepository,
        RedisCacheService cache,
        int maxImageBase64Chars,
        int maxMetadataJsonChars)
    {
        _aiClient = aiClient;
        _vectorBridge = vectorBridge;
        _captureRepository = captureRepository;
        _cache = cache;
        _maxImageBase64Chars = maxImageBase64Chars;
        _maxMetadataJsonChars = maxMetadataJsonChars;
    }

    public async Task<IResult> ExtractAsync(VectorExtractReq req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageBase64))
        {
            return AuraApiResults.BadRequest("图片Base64不能为空", 40051);
        }
        if (req.ImageBase64.Length > _maxImageBase64Chars)
        {
            return AuraApiResults.BadRequest("图片 Base64 过大", 40053);
        }
        if (!string.IsNullOrWhiteSpace(req.MetadataJson) && req.MetadataJson.Length > _maxMetadataJsonChars)
        {
            return AuraApiResults.BadRequest("元数据过大", 40054);
        }

        var metadataJson = req.MetadataJson ?? "{}";
        var cacheKey = BuildExtractCacheKey(req.ImageBase64, metadataJson);
        var cached = await TryGetCachedExtractAsync(cacheKey);
        if (cached is not null)
        {
            return Results.Ok(new { code = 0, msg = "提取成功", data = cached });
        }

        var ai = await _aiClient.ExtractAsync(req.ImageBase64, metadataJson);
        if (!ai.Success)
        {
            return AuraApiResults.BadRequest(ai.Message, 40052, new { ai.Dim });
        }

        var data = new VectorExtractPayload(ai.Dim, ai.Feature);
        await SetCachedAsync(cacheKey, data, ExtractCacheTtl);
        return Results.Ok(new { code = 0, msg = "提取成功", data });
    }

    public async Task<IResult> SearchAsync(VectorSearchReq req)
    {
        var topK = req.TopK <= 0 ? 10 : Math.Min(req.TopK, 50);
        if (req.Feature is null || req.Feature.Count == 0)
        {
            return AuraApiResults.BadRequest("特征向量不能为空", 40071);
        }
        if (req.Feature.Count != 512)
        {
            return AuraApiResults.BadRequest("特征向量维度必须为512", 40072);
        }
        if (req.Feature.Any(static x => !float.IsFinite(x)))
        {
            return AuraApiResults.BadRequest("特征向量包含无效数值", 40073);
        }

        var cacheKey = BuildSearchCacheKey(req.Feature, topK);
        var cached = await TryGetCachedSearchAsync(cacheKey);
        if (cached is not null)
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = cached });
        }

        IReadOnlyList<VectorIndexHit> rows;
        try
        {
            rows = await _vectorBridge.SearchAsync(req.Feature, topK);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AuraApiResults.BadGateway(ex.Message, 50271);
        }

        if (rows.Count == 0)
        {
            var empty = Array.Empty<VectorSearchHit>();
            await SetCachedAsync(cacheKey, empty, SearchCacheTtl);
            return Results.Ok(new { code = 0, msg = "查询成功", data = empty });
        }

        var vids = rows
            .Select(x => x.Vid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var imageMap = vids.Count > 0
            ? await _captureRepository.GetBestCaptureImageByVidsAsync(vids)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var data = rows
            .Select(x =>
            {
                var vid = x.Vid;
                return new VectorSearchHit(
                    vid,
                    x.Score,
                    imageMap.TryGetValue(vid, out var imageUrl) ? imageUrl : null);
            })
            .ToList();
        await SetCachedAsync(cacheKey, data, SearchCacheTtl);
        return Results.Ok(new { code = 0, msg = "查询成功", data });
    }

    private async Task<VectorExtractPayload?> TryGetCachedExtractAsync(string key)
    {
        if (!_cache.Enabled)
        {
            return null;
        }

        try
        {
            var cached = await _cache.GetAsync(key);
            if (string.IsNullOrWhiteSpace(cached))
            {
                return null;
            }

            return JsonSerializer.Deserialize<VectorExtractPayload>(cached, AuraJsonSerializerOptions.Default);
        }
        catch
        {
            await _cache.DeleteAsync(key);
            return null;
        }
    }

    private async Task<IReadOnlyList<VectorSearchHit>?> TryGetCachedSearchAsync(string key)
    {
        if (!_cache.Enabled)
        {
            return null;
        }

        try
        {
            var cached = await _cache.GetAsync(key);
            if (string.IsNullOrWhiteSpace(cached))
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<VectorSearchHit>>(cached, AuraJsonSerializerOptions.Default);
        }
        catch
        {
            await _cache.DeleteAsync(key);
            return null;
        }
    }

    private async Task SetCachedAsync(string key, object value, TimeSpan ttl)
    {
        if (!_cache.Enabled)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(value, AuraJsonSerializerOptions.Default);
            await _cache.SetAsync(key, json, ttl);
        }
        catch
        {
            // Cache failures must not block the vector path.
        }
    }

    private static string BuildExtractCacheKey(string imageBase64, string metadataJson)
    {
        var payload = $"{imageBase64.Length}|{metadataJson.Length}|{imageBase64}|{metadataJson}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        return $"{ExtractCachePrefix}:{hash}";
    }

    private static string BuildSearchCacheKey(IReadOnlyList<float> feature, int topK)
    {
        var buffer = new StringBuilder(feature.Count * 12);
        buffer.Append(topK).Append('|').Append(feature.Count).Append('|');
        foreach (var item in feature)
        {
            buffer.Append(item.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(buffer.ToString())))
            .ToLowerInvariant();
        return $"{SearchCachePrefix}:{hash}";
    }

    private sealed record VectorExtractPayload(int Dim, List<float> Feature);
    private sealed record VectorSearchHit(string vid, double score, string? imageUrl);
}
