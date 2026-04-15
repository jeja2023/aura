using Aura.Api.Ai;
using Aura.Api.Data;
using Aura.Api.Models;
using Microsoft.AspNetCore.Http;

internal sealed class VectorApplicationService
{
    private readonly AiClient _aiClient;
    private readonly PgSqlStore _db;
    private readonly int _maxImageBase64Chars;
    private readonly int _maxMetadataJsonChars;

    public VectorApplicationService(AiClient aiClient, PgSqlStore db, int maxImageBase64Chars, int maxMetadataJsonChars)
    {
        _aiClient = aiClient;
        _db = db;
        _maxImageBase64Chars = maxImageBase64Chars;
        _maxMetadataJsonChars = maxMetadataJsonChars;
    }

    public async Task<IResult> ExtractAsync(VectorExtractReq req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageBase64))
        {
            return Results.BadRequest(new { code = 40051, msg = "图片Base64不能为空" });
        }
        if (req.ImageBase64.Length > _maxImageBase64Chars)
        {
            return Results.BadRequest(new { code = 40053, msg = "图片 Base64 过大" });
        }
        if (!string.IsNullOrWhiteSpace(req.MetadataJson) && req.MetadataJson.Length > _maxMetadataJsonChars)
        {
            return Results.BadRequest(new { code = 40054, msg = "元数据过大" });
        }

        var ai = await _aiClient.ExtractAsync(req.ImageBase64, req.MetadataJson ?? "{}");
        if (!ai.Success)
        {
            return Results.BadRequest(new { code = 40052, msg = ai.Message, data = new { ai.Dim } });
        }
        return Results.Ok(new { code = 0, msg = "提取成功", data = new { ai.Dim, ai.Feature } });
    }

    public async Task<IResult> SearchAsync(VectorSearchReq req)
    {
        var topK = req.TopK <= 0 ? 10 : Math.Min(req.TopK, 50);
        if (req.Feature is null || req.Feature.Count == 0)
        {
            return Results.BadRequest(new { code = 40071, msg = "特征向量不能为空" });
        }
        if (req.Feature.Count != 512)
        {
            return Results.BadRequest(new { code = 40072, msg = "特征向量维度必须为 512" });
        }

        var rows = await _aiClient.SearchAsync(req.Feature, topK);
        if (rows.Count == 0)
        {
            return Results.Ok(new { code = 0, msg = "查询成功", data = rows });
        }

        var vids = rows
            .Select(x => x.vid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var imageMap = vids.Count > 0
            ? await _db.GetBestCaptureImageByVidsAsync(vids)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var data = rows.Select(x => new
        {
            x.vid,
            x.score,
            imageUrl = imageMap.TryGetValue(x.vid, out var imageUrl) ? imageUrl : null
        });
        return Results.Ok(new { code = 0, msg = "查询成功", data });
    }
}
