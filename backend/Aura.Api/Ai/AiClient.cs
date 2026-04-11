using System.Net.Http.Json;
using System.Text.Json;

namespace Aura.Api.Ai;

internal sealed class AiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger<AiClient> _logger;

    public AiClient(HttpClient httpClient, string baseUrl, ILogger<AiClient> logger)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
    }

    public async Task<JsonElement?> GetHealthAsync()
    {
        try
        {
            _logger.LogInformation("正在检查 AI 服务健康状态: {Url}", _baseUrl);
            var res = await _httpClient.GetAsync($"{_baseUrl}/");
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadFromJsonAsync<JsonElement>();
                _logger.LogInformation("AI 服务健康检查通过。");
                return json;
            }
            _logger.LogWarning("AI 服务响应异常：{StatusCode}", res.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接 AI 服务失败。");
            return null;
        }
    }

    public async Task<AiExtractResult> ExtractAsync(string imageBase64, string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return new AiExtractResult(false, 0, "图片为空，跳过AI提取", []);
        }

        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ai/extract", new
            {
                image_base64 = imageBase64,
                metadata_json = metadataJson
            });
            if (!res.IsSuccessStatusCode)
            {
                return new AiExtractResult(false, 0, $"AI服务响应异常：{(int)res.StatusCode}", []);
            }

            var json = await res.Content.ReadFromJsonAsync<AiExtractResponse>();
            var dim = json?.data?.dim ?? 0;
            var feature = json?.data?.feature ?? [];
            return new AiExtractResult(true, dim, "AI提取成功", feature);
        }
        catch (Exception ex)
        {
            return new AiExtractResult(false, 0, $"AI调用失败：{ex.Message}", []);
        }
    }

    public async Task<AiExtractResult> ExtractByPathAsync(string imagePath, string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return new AiExtractResult(false, 0, "图片路径为空，跳过AI提取", []);
        }

        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ai/extract-file", new
            {
                image_path = imagePath,
                metadata_json = metadataJson
            });
            if (!res.IsSuccessStatusCode)
            {
                return new AiExtractResult(false, 0, $"AI服务响应异常：{(int)res.StatusCode}", []);
            }

            var json = await res.Content.ReadFromJsonAsync<AiExtractResponse>();
            var dim = json?.data?.dim ?? 0;
            var feature = json?.data?.feature ?? [];
            return new AiExtractResult(true, dim, "AI提取成功", feature);
        }
        catch (Exception ex)
        {
            return new AiExtractResult(false, 0, $"AI调用失败：{ex.Message}", []);
        }
    }

    public async Task<bool> UpsertAsync(string vid, List<float> feature)
    {
        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ai/upsert", new { vid, feature });
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<AiSearchItem>> SearchAsync(List<float> feature, int topK)
    {
        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ai/search", new { feature, top_k = topK });
            if (!res.IsSuccessStatusCode)
            {
                return [];
            }
            var json = await res.Content.ReadFromJsonAsync<AiSearchResponse>();
            return json?.data ?? [];
        }
        catch
        {
            return [];
        }
    }
}

internal sealed record AiExtractResult(bool Success, int Dim, string Message, List<float> Feature);
internal sealed class AiExtractResponse
{
    public int code { get; set; }
    public string msg { get; set; } = string.Empty;
    public AiExtractData? data { get; set; }
}

internal sealed class AiExtractData
{
    public int dim { get; set; }
    public List<float>? feature { get; set; }
}

internal sealed class AiSearchResponse
{
    public int code { get; set; }
    public string msg { get; set; } = string.Empty;
    public List<AiSearchItem>? data { get; set; }
}

internal sealed record AiSearchItem(string vid, double score);
