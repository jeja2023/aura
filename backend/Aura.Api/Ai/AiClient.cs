using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aura.Api.Ai;

internal sealed class AiClient
{
    private const string DefaultBaseUrl = "http://127.0.0.1:8000";

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<string> _staticBaseUrls;
    private readonly AiRuntimeOptionsProvider? _runtimeOptionsProvider;
    private readonly ILogger<AiClient> _logger;
    private int _nextEndpointIndex = -1;

    public AiClient(HttpClient httpClient, string baseUrl, ILogger<AiClient> logger)
        : this(httpClient, [baseUrl], logger)
    {
    }

    public AiClient(HttpClient httpClient, IEnumerable<string?> baseUrls, ILogger<AiClient> logger)
    {
        _httpClient = httpClient;
        _staticBaseUrls = NormalizeBaseUrls(baseUrls, DefaultBaseUrl);
        _runtimeOptionsProvider = null;
        _logger = logger;
    }

    public AiClient(HttpClient httpClient, AiRuntimeOptionsProvider runtimeOptionsProvider, ILogger<AiClient> logger)
    {
        _httpClient = httpClient;
        _staticBaseUrls = runtimeOptionsProvider.FallbackBaseUrls;
        _runtimeOptionsProvider = runtimeOptionsProvider;
        _logger = logger;
    }

    internal IReadOnlyList<string> BaseUrls => _staticBaseUrls;

    internal static IReadOnlyList<string> ResolveBaseUrls(string? baseUrls, string? baseUrl, string fallbackBaseUrl = DefaultBaseUrl)
    {
        var configuredBaseUrls = SplitBaseUrlValue(baseUrls).ToList();
        var values = configuredBaseUrls.Count > 0
            ? configuredBaseUrls
            : SplitBaseUrlValue(baseUrl);
        return NormalizeBaseUrls(values, fallbackBaseUrl);
    }

    internal static IReadOnlyList<string> NormalizeBaseUrls(IEnumerable<string?> values, string fallbackBaseUrl = DefaultBaseUrl)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in values.SelectMany(SplitBaseUrlValue))
        {
            AddBaseUrl(raw, result, seen);
        }

        if (result.Count == 0)
        {
            AddBaseUrl(fallbackBaseUrl, result, seen);
        }

        return result;
    }

    internal static IEnumerable<string> SplitBaseUrlValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var item in value.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                yield return item;
            }
        }
    }

    public async Task<JsonElement?> GetHealthAsync()
    {
        var health = await GetClusterHealthAsync();
        return health.BestNode?.Payload;
    }

    public async Task<AiClusterHealth> GetClusterHealthAsync()
    {
        var baseUrls = await GetCurrentBaseUrlsAsync();
        var tasks = baseUrls.Select(GetEndpointHealthAsync).ToArray();
        var nodes = await Task.WhenAll(tasks);
        return new AiClusterHealth(nodes);
    }

    public async Task<AiExtractResult> ExtractAsync(string imageBase64, string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return new AiExtractResult(false, 0, "图片为空，跳过AI提取", []);
        }

        var result = await PostJsonWithFailoverAsync<AiExtractResponse>("/ai/extract", new
        {
            image_base64 = imageBase64,
            metadata_json = metadataJson
        });
        if (result.Exception is not null)
        {
            return new AiExtractResult(false, 0, BuildExceptionMessage("AI调用失败", result), []);
        }

        var json = result.Json;
        if (!result.IsSuccessStatusCode)
        {
            return new AiExtractResult(false, 0, BuildFailureMessage("AI服务响应异常", result, json?.msg), []);
        }

        if (json is null)
        {
            return new AiExtractResult(false, 0, BuildFailureMessage("AI服务返回了无法解析的响应", result, null), []);
        }

        if (json.code != 0)
        {
            return new AiExtractResult(false, json.data?.dim ?? 0, BuildFailureMessage("AI提取失败", result, json.msg, json.code), json.data?.feature ?? []);
        }

        var dim = json.data?.dim ?? 0;
        var feature = json.data?.feature ?? [];
        if (dim <= 0 || feature.Count == 0)
        {
            return new AiExtractResult(false, dim, BuildFailureMessage("AI提取结果为空", result, null), feature);
        }

        return new AiExtractResult(true, dim, "AI提取成功", feature);
    }

    public async Task<AiExtractResult> ExtractByPathAsync(string imagePath, string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return new AiExtractResult(false, 0, "图片路径为空，跳过AI提取", []);
        }

        var result = await PostJsonWithFailoverAsync<AiExtractResponse>("/ai/extract-file", new
        {
            image_path = imagePath,
            metadata_json = metadataJson
        });
        if (result.Exception is not null)
        {
            return new AiExtractResult(false, 0, BuildExceptionMessage("AI调用失败", result), []);
        }

        var json = result.Json;
        if (!result.IsSuccessStatusCode)
        {
            return new AiExtractResult(false, 0, BuildFailureMessage("AI服务响应异常", result, json?.msg), []);
        }

        if (json is null)
        {
            return new AiExtractResult(false, 0, BuildFailureMessage("AI服务返回了无法解析的响应", result, null), []);
        }

        if (json.code != 0)
        {
            return new AiExtractResult(false, json.data?.dim ?? 0, BuildFailureMessage("AI提取失败", result, json.msg, json.code), json.data?.feature ?? []);
        }

        var dim = json.data?.dim ?? 0;
        var feature = json.data?.feature ?? [];
        if (dim <= 0 || feature.Count == 0)
        {
            return new AiExtractResult(false, dim, BuildFailureMessage("AI提取结果为空", result, null), feature);
        }

        return new AiExtractResult(true, dim, "AI提取成功", feature);
    }

    public async Task<AiUpsertResult> UpsertAsync(string vid, List<float> feature)
    {
        var result = await PostJsonWithFailoverAsync<AiUpsertResponse>("/ai/upsert", new { vid, feature });
        if (result.Exception is not null)
        {
            return new AiUpsertResult(false, BuildExceptionMessage("AI向量写入调用失败", result));
        }

        var json = result.Json;
        if (!result.IsSuccessStatusCode)
        {
            return new AiUpsertResult(false, BuildFailureMessage("AI向量写入失败", result, json?.msg), json?.data?.engine);
        }

        if (json is null)
        {
            return new AiUpsertResult(false, BuildFailureMessage("AI向量写入返回了无法解析的响应", result, null));
        }

        if (json.code != 0)
        {
            return new AiUpsertResult(false, BuildFailureMessage("AI向量写入失败", result, json.msg, json.code), json.data?.engine);
        }

        return new AiUpsertResult(true, json.msg, json.data?.engine);
    }

    public async Task<AiSearchResult> SearchAsync(List<float> feature, int topK)
    {
        var result = await PostJsonWithFailoverAsync<AiSearchResponse>("/ai/search", new { feature, top_k = topK });
        if (result.Exception is not null)
        {
            return new AiSearchResult(false, BuildExceptionMessage("AI检索调用失败", result), []);
        }

        var json = result.Json;
        if (!result.IsSuccessStatusCode)
        {
            return new AiSearchResult(false, BuildFailureMessage("AI检索失败", result, json?.msg), []);
        }

        if (json is null)
        {
            return new AiSearchResult(false, BuildFailureMessage("AI检索返回了无法解析的响应", result, null), []);
        }

        if (json.code != 0)
        {
            return new AiSearchResult(false, BuildFailureMessage("AI检索失败", result, json.msg, json.code), []);
        }

        return new AiSearchResult(true, json.msg, json.data ?? []);
    }

    public async Task<AiSearchStatsResult> GetSearchStatsAsync(int windowMinutes = 15)
    {
        var minutes = Math.Max(0, windowMinutes);
        var result = await GetJsonWithFailoverAsync<AiSearchStatsResponse>($"/ai/search-stats?window_minutes={minutes}");
        if (result.Exception is not null)
        {
            return new AiSearchStatsResult(false, BuildExceptionMessage("AI检索指标调用失败", result), null);
        }

        var json = result.Json;
        if (!result.IsSuccessStatusCode)
        {
            return new AiSearchStatsResult(false, BuildFailureMessage("AI检索指标查询失败", result, json?.msg), null);
        }

        if (json is null)
        {
            return new AiSearchStatsResult(false, BuildFailureMessage("AI检索指标返回了无法解析的响应", result, null), null);
        }

        if (json.code != 0)
        {
            return new AiSearchStatsResult(false, BuildFailureMessage("AI检索指标查询失败", result, json.msg, json.code), null);
        }

        return new AiSearchStatsResult(true, json.msg, json.data);
    }

    private async Task<AiEndpointHealth> GetEndpointHealthAsync(string baseUrl)
    {
        try
        {
            using var res = await _httpClient.GetAsync($"{baseUrl}/");
            JsonElement? payload = null;
            try
            {
                payload = await res.Content.ReadFromJsonAsync<JsonElement>();
            }
            catch
            {
                // 健康接口不可解析时仍记录 HTTP 状态，便于运维定位节点。
            }

            var code = TryGetInt(payload, "code");
            var message = TryGetString(payload, "msg") ?? string.Empty;
            var modelLoaded = TryGetBool(payload, "model_loaded") == true;
            var reachable = res.IsSuccessStatusCode && payload.HasValue;
            return new AiEndpointHealth
            {
                BaseUrl = baseUrl,
                Reachable = reachable,
                ModelLoaded = reachable && modelLoaded,
                StatusCode = (int)res.StatusCode,
                Code = code,
                Message = message,
                Error = reachable ? string.Empty : $"HTTP {(int)res.StatusCode}",
                Payload = payload
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 节点健康检查失败：{BaseUrl}", baseUrl);
            return new AiEndpointHealth
            {
                BaseUrl = baseUrl,
                Reachable = false,
                ModelLoaded = false,
                StatusCode = null,
                Code = null,
                Message = string.Empty,
                Error = ex.Message,
                Payload = null
            };
        }
    }

    private Task<AiHttpResult<T>> GetJsonWithFailoverAsync<T>(string path) where T : class =>
        SendJsonWithFailoverAsync<T>(baseUrl => _httpClient.GetAsync($"{baseUrl}{path}"));

    private Task<AiHttpResult<T>> PostJsonWithFailoverAsync<T>(string path, object payload) where T : class =>
        SendJsonWithFailoverAsync<T>(baseUrl => _httpClient.PostAsJsonAsync($"{baseUrl}{path}", payload));

    private async Task<AiHttpResult<T>> SendJsonWithFailoverAsync<T>(Func<string, Task<HttpResponseMessage>> sendAsync) where T : class
    {
        AiHttpResult<T>? lastResult = null;
        var baseUrls = await GetCurrentBaseUrlsAsync();
        var start = unchecked((uint)Interlocked.Increment(ref _nextEndpointIndex));

        for (var attempt = 0; attempt < baseUrls.Count; attempt++)
        {
            var baseUrl = baseUrls[(int)((start + (uint)attempt) % (uint)baseUrls.Count)];
            try
            {
                using var response = await sendAsync(baseUrl);
                var json = await ReadJsonAsync<T>(response);
                var result = new AiHttpResult<T>(
                    BaseUrl: baseUrl,
                    StatusCode: response.StatusCode,
                    IsSuccessStatusCode: response.IsSuccessStatusCode,
                    Json: json,
                    Exception: null);

                if (attempt + 1 < baseUrls.Count && IsRetryableStatus(response.StatusCode))
                {
                    _logger.LogWarning("AI 节点响应可重试状态，切换到下一个节点。endpoint={BaseUrl}, http={StatusCode}", baseUrl, (int)response.StatusCode);
                    lastResult = result;
                    continue;
                }

                return result;
            }
            catch (Exception ex) when (attempt + 1 < baseUrls.Count && IsTransientRequestException(ex))
            {
                _logger.LogWarning(ex, "AI 节点暂不可用，切换到下一个节点。endpoint={BaseUrl}", baseUrl);
                lastResult = new AiHttpResult<T>(
                    BaseUrl: baseUrl,
                    StatusCode: null,
                    IsSuccessStatusCode: false,
                    Json: null,
                    Exception: ex);
            }
            catch (Exception ex)
            {
                return new AiHttpResult<T>(
                    BaseUrl: baseUrl,
                    StatusCode: null,
                    IsSuccessStatusCode: false,
                    Json: null,
                    Exception: ex);
            }
        }

        return lastResult ?? new AiHttpResult<T>(
            BaseUrl: baseUrls[0],
            StatusCode: null,
            IsSuccessStatusCode: false,
            Json: null,
            Exception: new InvalidOperationException("未配置可用的 AI 节点"));
    }

    private async Task<IReadOnlyList<string>> GetCurrentBaseUrlsAsync()
    {
        if (_runtimeOptionsProvider is null)
        {
            return _staticBaseUrls;
        }

        var options = await _runtimeOptionsProvider.GetAsync().ConfigureAwait(false);
        return options.BaseUrls.Count > 0 ? options.BaseUrls : _staticBaseUrls;
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

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.TooManyRequests;

    private static bool IsTransientRequestException(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;

    private static string BuildExceptionMessage<T>(string prefix, AiHttpResult<T> result) where T : class
    {
        var detail = result.Exception?.Message;
        return BuildFailureMessage(prefix, result, detail);
    }

    private static string BuildFailureMessage<T>(string prefix, AiHttpResult<T> result, string? detail, int? code = null) where T : class
    {
        var parts = new List<string> { prefix };
        if (code.HasValue && code.Value != 0)
        {
            parts.Add($"code={code.Value}");
        }

        if (result.StatusCode.HasValue)
        {
            parts.Add($"http={(int)result.StatusCode.Value}");
        }

        if (!string.IsNullOrWhiteSpace(result.BaseUrl))
        {
            parts.Add($"endpoint={result.BaseUrl}");
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            parts.Add(detail.Trim());
        }

        return string.Join("；", parts);
    }

    private static void AddBaseUrl(string raw, List<string> result, HashSet<string> seen)
    {
        var value = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"AI 服务地址无效：{raw}");
        }

        if (seen.Add(value))
        {
            result.Add(value);
        }
    }

    private static int? TryGetInt(JsonElement? payload, string propertyName)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.Value.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TryGetString(JsonElement? payload, string propertyName)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return payload.Value.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? TryGetBool(JsonElement? payload, string propertyName)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.Value.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record AiHttpResult<T>(
        string BaseUrl,
        HttpStatusCode? StatusCode,
        bool IsSuccessStatusCode,
        T? Json,
        Exception? Exception) where T : class;
}

internal sealed record AiExtractResult(bool Success, int Dim, string Message, List<float> Feature);
internal sealed record AiUpsertResult(bool Success, string Message, string? Engine = null);
internal sealed record AiSearchResult(bool Success, string Message, List<AiSearchItem> Items);
internal sealed record AiSearchStatsResult(bool Success, string Message, AiSearchStatsData? Data);

internal sealed class AiClusterHealth
{
    public AiClusterHealth(IReadOnlyList<AiEndpointHealth> nodes)
    {
        Nodes = nodes;
    }

    public IReadOnlyList<AiEndpointHealth> Nodes { get; }
    public int ConfiguredNodeCount => Nodes.Count;
    public int ReachableNodeCount => Nodes.Count(x => x.Reachable);
    public int ModelLoadedNodeCount => Nodes.Count(x => x.ModelLoaded);
    public bool AnyReachable => ReachableNodeCount > 0;
    public bool AnyModelLoaded => ModelLoadedNodeCount > 0;

    [JsonIgnore]
    public AiEndpointHealth? BestNode =>
        Nodes.FirstOrDefault(x => x.Reachable && x.ModelLoaded)
        ?? Nodes.FirstOrDefault(x => x.Reachable);
}

internal sealed class AiEndpointHealth
{
    public string BaseUrl { get; init; } = string.Empty;
    public bool Reachable { get; init; }
    public bool ModelLoaded { get; init; }
    public int? StatusCode { get; init; }
    public int? Code { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    [JsonIgnore]
    public JsonElement? Payload { get; init; }
}

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
