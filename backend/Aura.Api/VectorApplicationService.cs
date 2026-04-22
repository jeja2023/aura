using Aura.Api.Ai;
using Aura.Api.Data;
using Aura.Api.Internal;
using Aura.Api.Models;
using Microsoft.AspNetCore.Http;

internal sealed class VectorApplicationService
{
    private readonly AiClient _aiClient;
    private readonly CaptureRepository _captureRepository;
    private readonly int _maxImageBase64Chars;
    private readonly int _maxMetadataJsonChars;

    public VectorApplicationService(AiClient aiClient, CaptureRepository captureRepository, int maxImageBase64Chars, int maxMetadataJsonChars)
    {
        _aiClient = aiClient;
        _captureRepository = captureRepository;
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

        var ai = await _aiClient.ExtractAsync(req.ImageBase64, req.MetadataJson ?? "{}");
        if (!ai.Success)
        {
            return AuraApiResults.BadRequest(ai.Message, 40052, new { ai.Dim });
        }
        return Results.Ok(new { code = 0, msg = "提取成功", data = new { ai.Dim, ai.Feature } });
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
            return AuraApiResults.BadRequest("特征向量维度必须为 512", 40072);
        }

        var rows = await _aiClient.SearchAsync(req.Feature, topK);
        if (!rows.Success)
        {
            return AuraApiResults.BadGateway(rows.Message, 50271);
        }

        if (rows.Items.Count == 0)
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows.Items });
        }

        var vids = rows.Items
            .Select(x => x.vid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var imageMap = vids.Count > 0
            ? await _captureRepository.GetBestCaptureImageByVidsAsync(vids)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var data = rows.Items.Select(x => new
        {
            x.vid,
            x.score,
            imageUrl = imageMap.TryGetValue(x.vid, out var imageUrl) ? imageUrl : null
        });
        return Results.Ok(new { code = 0, msg = "查询成功", data });
    }
}
