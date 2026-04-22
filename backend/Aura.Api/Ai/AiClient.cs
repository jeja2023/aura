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
            var json = await ReadJsonAsync<AiExtractResponse>(res);
            if (!res.IsSuccessStatusCode)
            {
                return new AiExtractResult(false, 0, BuildFailureMessage("AI服务响应异常", res, json?.msg), []);
            }

            if (json is null)
            {
                return new AiExtractResult(false, 0, "AI服务返回了无法解析的响应", []);
            }

            if (json.code != 0)
            {
                return new AiExtractResult(false, json.data?.dim ?? 0, BuildFailureMessage("AI提取失败", null, json.msg, json.code), json.data?.feature ?? []);
            }

            var dim = json?.data?.dim ?? 0;
            var feature = json?.data?.feature ?? [];
            if (dim <= 0 || feature.Count == 0)
            {
                return new AiExtractResult(false, dim, "AI提取结果为空", feature);
            }

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
            var json = await ReadJsonAsync<AiExtractResponse>(res);
            if (!res.IsSuccessStatusCode)
            {
                return new AiExtractResult(false, 0, BuildFailureMessage("AI服务响应异常", res, json?.msg), []);
            }

            if (json is null)
            {
                return new AiExtractResult(false, 0, "AI服务返回了无法解析的响应", []);
            }

            if (json.code != 0)
            {
                return new AiExtractResult(false, json.data?.dim ?? 0, BuildFailureMessage("AI提取失败", null, json.msg, json.code), json.data?.feature ?? []);
            }

            var dim = json?.data?.dim ?? 0;
            var feature = json?.data?.feature ?? [];
            if (dim <= 0 || feature.Count == 0)
            {
                return new AiExtractResult(false, dim, "AI提取结果为空", feature);
            }

            return new AiExtractResult(true, dim, "AI提取成功", feature);
        }
        catch (Exception ex)
        {
            return new AiExtractResult(false, 0, $"AI调用失败：{ex.Message}", []);
        }
    }

    public async Task<AiUpsertResult> UpsertAsync(string vid, List<float> feature)
    {
        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ai/upsert", new { vid, feature });
            var json = await ReadJsonAsync<AiUpsertResponse>(res);
            if (!res.IsSuccessStatusCode)
            {
                return new AiUpsertResult(false, BuildFailureMessage("AI向量写入失败", res, json?.msg), json?.data?.engine);
            }

            if (json is null)
            {
                return new AiUpsertResult(false, "AI向量写入返回了无法解析的响应");
            }

            if (json.code != 0)
            {
                return new AiUpsertResult(false, BuildFailureMessage("AI向量写入失败", null, json.msg, json.code), json.data?.engine);
            }

            return new AiUpsertResult(true, json.msg, json.data?.engine);
        }
        catch (Exception ex)
        {
            return new AiUpsertResult(false, $"AI向量写入调用失败：{ex.Message}");
        }
    }

    public async Task<AiSearchResult> SearchAsync(List<float> feature, int topK)
    {
        try
        {
            var res = await _httpClient.PostAsJsonAsync($"{_baseUrl}/ai/search", new { feature, top_k = topK });
            var json = await ReadJsonAsync<AiSearchResponse>(res);
            if (!res.IsSuccessStatusCode)
            {
                return new AiSearchResult(false, BuildFailureMessage("AI检索失败", res, json?.msg), []);
            }

            if (json is null)
            {
                return new AiSearchResult(false, "AI检索返回了无法解析的响应", []);
            }

            if (json.code != 0)
            {
                return new AiSearchResult(false, BuildFailureMessage("AI检索失败", null, json.msg, json.code), []);
            }

            return new AiSearchResult(true, json.msg, json.data ?? []);
        }
        catch (Exception ex)
        {
            return new AiSearchResult(false, $"AI检索调用失败：{ex.Message}", []);
        }
    }

    public async Task<AiSearchStatsResult> GetSearchStatsAsync(int windowMinutes = 15)
    {
        try
        {
            var minutes = Math.Max(0, windowMinutes);
            var res = await _httpClient.GetAsync($"{_baseUrl}/ai/search-stats?window_minutes={minutes}");
            var json = await ReadJsonAsync<AiSearchStatsResponse>(res);
            if (!res.IsSuccessStatusCode)
            {
                return new AiSearchStatsResult(false, BuildFailureMessage("AI检索指标查询失败", res, json?.msg), null);
            }

            if (json is null)
            {
                return new AiSearchStatsResult(false, "AI检索指标返回了无法解析的响应", null);
            }

            if (json.code != 0)
            {
                return new AiSearchStatsResult(false, BuildFailureMessage("AI检索指标查询失败", null, json.msg, json.code), null);
            }

            return new AiSearchStatsResult(true, json.msg, json.data);
        }
        catch (Exception ex)
        {
            return new AiSearchStatsResult(false, $"AI检索指标调用失败：{ex.Message}", null);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFailureMessage(string prefix, HttpResponseMessage? response, string? detail, int? code = null)
    {
        var parts = new List<string> { prefix };
        if (code.HasValue && code.Value != 0)
        {
            parts.Add($"code={code.Value}");
        }

        if (response is not null)
        {
            parts.Add($"http={(int)response.StatusCode}");
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail.Trim());
        }

        return string.Join("：", parts);
    }
}

internal sealed record AiExtractResult(bool Success, int Dim, string Message, List<float> Feature);
internal sealed record AiUpsertResult(bool Success, string Message, string? Engine = null);
internal sealed record AiSearchResult(bool Success, string Message, List<AiSearchItem> Items);
internal sealed record AiSearchStatsResult(bool Success, string Message, AiSearchStatsData? Data);

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

internal sealed class AiUpsertResponse
{
    public int code { get; set; }
    public string msg { get; set; } = string.Empty;
    public AiUpsertData? data { get; set; }
}

internal sealed class AiUpsertData
{
    public string? vid { get; set; }
    public string? engine { get; set; }
}

internal sealed record AiSearchItem(string vid, double score);

internal sealed class AiSearchStatsResponse
{
    public int code { get; set; }
    public string msg { get; set; } = string.Empty;
    public AiSearchStatsData? data { get; set; }
}

internal sealed class AiSearchStatsData
{
    public int search_total { get; set; }
    public int search_success { get; set; }
    public int search_failed { get; set; }
    public int search_empty { get; set; }
    public double search_avg_latency_ms { get; set; }
    public string? last_search_time { get; set; }
    public AiSearchStatsWindow? window { get; set; }
}

internal sealed class AiSearchStatsWindow
{
    public int window_minutes { get; set; }
    public int search_total { get; set; }
    public int search_success { get; set; }
    public int search_failed { get; set; }
    public int search_empty { get; set; }
    public double search_avg_latency_ms { get; set; }
}
