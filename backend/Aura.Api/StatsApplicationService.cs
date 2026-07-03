using System.Text.Json;
using Aura.Api.Ai;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Serialization;

internal sealed class StatsApplicationService
{
    private const string OverviewCacheKey = "stats:overview:v1";
    private const string DashboardCacheKey = "stats:dashboard:v1";
    private static readonly TimeSpan OverviewCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromSeconds(30);

    private readonly AppStore _store;
    private readonly PgSqlConnectionFactory _pgSqlConnectionFactory;
    private readonly CaptureRepository _captureRepository;
    private readonly MonitoringRepository _monitoringRepository;
    private readonly DeviceRepository _deviceRepository;
    private readonly RetryQueueService _retryQueue;
    private readonly AiClient _aiClient;
    private readonly RedisCacheService _cache;

    public StatsApplicationService(
        AppStore store,
        PgSqlConnectionFactory pgSqlConnectionFactory,
        CaptureRepository captureRepository,
        MonitoringRepository monitoringRepository,
        DeviceRepository deviceRepository,
        RetryQueueService retryQueue,
        AiClient aiClient,
        RedisCacheService cache)
    {
        _store = store;
        _pgSqlConnectionFactory = pgSqlConnectionFactory;
        _captureRepository = captureRepository;
        _monitoringRepository = monitoringRepository;
        _deviceRepository = deviceRepository;
        _retryQueue = retryQueue;
        _aiClient = aiClient;
        _cache = cache;
    }

    public async Task<object> GetOverviewAsync()
    {
        var cached = await TryGetCachedAsync(OverviewCacheKey);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        var totalCaptureDb = await _captureRepository.GetCaptureCountAsync();
        var totalAlertDb = await _monitoringRepository.GetAlertCountAsync();
        var devices = await _deviceRepository.GetDevicesAsync();
        var useDb = _pgSqlConnectionFactory.IsConfigured;

        if (useDb && (!totalCaptureDb.HasValue || !totalAlertDb.HasValue))
        {
            throw new InvalidOperationException("统计概览数据库查询失败。");
        }

        var totalCapture = useDb
            ? Math.Max(0, (int)totalCaptureDb!.Value)
            : totalCaptureDb.HasValue && totalCaptureDb.Value >= 0
                ? totalCaptureDb.Value
                : _store.Captures.Count;
        var totalAlert = useDb
            ? Math.Max(0, (int)totalAlertDb!.Value)
            : totalAlertDb.HasValue && totalAlertDb.Value >= 0
                ? totalAlertDb.Value
                : _store.Alerts.Count;
        var onlineDevice = useDb
            ? devices.Count(x => x.Status == "online")
            : devices.Count > 0
                ? devices.Count(x => x.Status == "online")
                : _store.Devices.Count(x => x.Status == "online");

        var ai = await BuildAiOverviewAsync();
        var result = new { totalCapture, totalAlert, onlineDevice, ai };
        await SetCachedAsync(OverviewCacheKey, result, OverviewCacheTtl);
        return result;
    }

    public async Task<object> GetDashboardAsync()
    {
        var cached = await TryGetCachedAsync(DashboardCacheKey);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);
        var end = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var rangeStart = new DateTimeOffset(start);
        var rangeEnd = new DateTimeOffset(end);

        var captures = await GetCaptureSamplesAsync(rangeStart, rangeEnd);
        var useDb = _pgSqlConnectionFactory.IsConfigured;
        var alertResult = await _monitoringRepository.GetAlertsPagedAsync(null, null, rangeStart, rangeEnd, 1, 1000);
        if (useDb && !alertResult.Succeeded)
        {
            throw new InvalidOperationException("统计图表告警查询失败。");
        }

        var alerts = alertResult.Rows;
        var sourceAlerts = useDb
            ? alerts.Select(x => new { x.AlertType, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
            : alerts.Count > 0
                ? alerts.Select(x => new { x.AlertType, CreatedAt = new DateTimeOffset(x.CreatedAt) }).ToList()
                : _store.Alerts
                    .Where(x => x.CreatedAt >= rangeStart && x.CreatedAt < rangeEnd)
                    .Select(x => new { x.AlertType, x.CreatedAt })
                    .ToList();

        var daily = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(-6 + i))
            .Select(d => new
            {
                day = d.ToString("MM-dd"),
                captureCount = captures.Count(x => DateOnly.FromDateTime(x.CaptureTime.DateTime) == d),
                alertCount = sourceAlerts.Count(x => DateOnly.FromDateTime(x.CreatedAt.DateTime) == d)
            })
            .ToList();

        var byDevice = captures
            .GroupBy(x => x.DeviceId)
            .Select(g => new { deviceId = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(10)
            .ToList();

        var byAlertType = sourceAlerts
            .GroupBy(x => string.IsNullOrWhiteSpace(x.AlertType) ? "unknown" : x.AlertType)
            .Select(g => new { alertType = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        var trackedAi = captures
            .Select(x => new { Day = DateOnly.FromDateTime(x.CaptureTime.DateTime), Meta = ParseCaptureAiMetadata(x.MetadataJson) })
            .Where(x => x.Meta.Tracked)
            .ToList();

        var aiDaily = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(-6 + i))
            .Select(d => new
            {
                day = d.ToString("MM-dd"),
                readyCount = trackedAi.Count(x => x.Day == d && x.Meta.Status == "ready"),
                retryPendingCount = trackedAi.Count(x => x.Day == d && IsRetryPending(x.Meta.Status)),
                failedCount = trackedAi.Count(x => x.Day == d && IsHardFailure(x.Meta.Status)),
                extractOnlyCount = trackedAi.Count(x => x.Day == d && x.Meta.Status == "extract_only")
            })
            .ToList();

        var aiStatus = trackedAi
            .GroupBy(x => x.Meta.Status)
            .Select(g => new
            {
                status = g.Key,
                label = GetAiStatusLabel(g.Key),
                count = g.Count()
            })
            .OrderBy(x => GetAiStatusSortOrder(x.status))
            .ThenByDescending(x => x.count)
            .ToList();

        var result = new { daily, byDevice, byAlertType, aiDaily, aiStatus };
        await SetCachedAsync(DashboardCacheKey, result, DashboardCacheTtl);
        return result;
    }

    private async Task<JsonElement?> TryGetCachedAsync(string key)
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

            return JsonSerializer.Deserialize<JsonElement>(cached, AuraJsonSerializerOptions.Default);
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
            // 缓存写入失败不影响统计接口主流程。
        }
    }

    private async Task<object> BuildAiOverviewAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var rangeStart = new DateTimeOffset(today.AddDays(-6).ToDateTime(TimeOnly.MinValue));
        var rangeEnd = new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var captures = await GetCaptureSamplesAsync(rangeStart, rangeEnd);
        var aiStates = captures
            .Select(x => ParseCaptureAiMetadata(x.MetadataJson))
            .Where(x => x.Tracked)
            .ToList();

        var trackedCaptureTotal = aiStates.Count;
        var readyCount = aiStates.Count(x => x.Status == "ready");
        var retryPendingCount = aiStates.Count(x => IsRetryPending(x.Status));
        var failureCount = aiStates.Count(x => IsHardFailure(x.Status));
        var vectorIssueCount = aiStates.Count(x => IsVectorIssue(x.Status));
        var extractOnlyCount = aiStates.Count(x => x.Status == "extract_only");
        var retryQueuePending = (int)Math.Max(0, await _retryQueue.LengthAsync());

        var searchStats = await _aiClient.GetSearchStatsAsync(windowMinutes: 15);
        var searchWindow = searchStats.Data?.window;
        var searchTotal = searchWindow?.search_total ?? 0;
        var searchFailed = searchWindow?.search_failed ?? 0;
        var searchEmpty = searchWindow?.search_empty ?? 0;
        var searchAvgLatencyMs = searchWindow?.search_avg_latency_ms ?? 0d;

        return new
        {
            captureWindowDays = 7,
            trackedCaptureTotal,
            readyCount,
            retryPendingCount,
            failureCount,
            failureRate = ComputeRatePercent(failureCount, trackedCaptureTotal),
            vectorIssueCount,
            vectorIssueRate = ComputeRatePercent(vectorIssueCount, trackedCaptureTotal),
            extractOnlyCount,
            retryQueuePending,
            retryQueueEnabled = _retryQueue.Enabled,
            searchAvailable = searchStats.Success,
            searchMessage = searchStats.Message,
            searchWindowMinutes = searchWindow?.window_minutes ?? 15,
            searchTotal,
            searchFailed,
            searchFailureRate = ComputeRatePercent(searchFailed, searchTotal),
            searchEmpty,
            searchAvgLatencyMs = Math.Round(searchAvgLatencyMs, 1)
        };
    }

    private async Task<List<StatsCaptureSample>> GetCaptureSamplesAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        var result = await _captureRepository.GetCapturesPagedAsync(rangeStart, rangeEnd, 1, 1000);
        var useDb = _pgSqlConnectionFactory.IsConfigured;

        if (useDb)
        {
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("统计图表抓拍查询失败。");
            }

            return result.Rows.Select(MapDbCapture).ToList();
        }

        if (result.Rows.Count > 0)
        {
            return result.Rows.Select(MapDbCapture).ToList();
        }

        return _store.Captures
            .Where(x => x.CaptureTime >= rangeStart && x.CaptureTime < rangeEnd)
            .Select(x => new StatsCaptureSample(x.DeviceId, x.CaptureTime, x.MetadataJson))
            .ToList();
    }

    private static StatsCaptureSample MapDbCapture(DbCapture capture)
    {
        var localTime = new DateTimeOffset(DateTime.SpecifyKind(capture.CaptureTime, DateTimeKind.Utc)).ToLocalTime();
        return new StatsCaptureSample(capture.DeviceId, localTime, capture.MetadataJson);
    }

    private static CaptureAiMetadata ParseCaptureAiMetadata(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return CaptureAiMetadata.Untracked;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CaptureAiMetadata.Untracked;
            }

            var root = doc.RootElement;
            var hasAiFields = TryGetProperty(root, "ai_status", out _) ||
                              TryGetProperty(root, "ai_success", out _) ||
                              TryGetProperty(root, "ai_vector_success", out _) ||
                              TryGetProperty(root, "ai_retry_queued", out _);
            if (!hasAiFields)
            {
                return CaptureAiMetadata.Untracked;
            }

            var status = TryGetString(root, "ai_status");
            var aiSuccess = TryGetBool(root, "ai_success");
            var vectorSuccess = TryGetBool(root, "ai_vector_success");
            var retryQueued = TryGetBool(root, "ai_retry_queued");

            if (string.IsNullOrWhiteSpace(status))
            {
                status = aiSuccess switch
                {
                    false => retryQueued == true ? "extract_retry_pending" : "extract_failed",
                    true when vectorSuccess == true => "ready",
                    true when retryQueued == true => "vector_retry_pending",
                    true => "extract_only",
                    _ => "unknown"
                };
            }

            return new CaptureAiMetadata(true, status.Trim(), aiSuccess, vectorSuccess, retryQueued);
        }
        catch
        {
            return CaptureAiMetadata.Untracked;
        }
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? TryGetBool(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
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

    private static bool IsRetryPending(string status) =>
        string.Equals(status, "extract_retry_pending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "vector_retry_pending", StringComparison.OrdinalIgnoreCase);

    private static bool IsHardFailure(string status) =>
        string.Equals(status, "extract_failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "vector_failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsVectorIssue(string status) =>
        string.Equals(status, "vector_failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "vector_retry_pending", StringComparison.OrdinalIgnoreCase);

    private static double ComputeRatePercent(int numerator, int denominator) =>
        denominator <= 0 ? 0d : Math.Round(numerator * 100d / denominator, 1);

    private static string GetAiStatusLabel(string status) => status switch
    {
        "ready" => "AI+向量就绪",
        "extract_only" => "仅提特征",
        "extract_retry_pending" => "提特征待重试",
        "vector_retry_pending" => "向量待补偿",
        "extract_failed" => "提特征失败",
        "vector_failed" => "向量失败",
        _ => "其他"
    };

    private static int GetAiStatusSortOrder(string status) => status switch
    {
        "ready" => 0,
        "extract_only" => 1,
        "extract_retry_pending" => 2,
        "vector_retry_pending" => 3,
        "extract_failed" => 4,
        "vector_failed" => 5,
        _ => 99
    };

    private sealed record StatsCaptureSample(long DeviceId, DateTimeOffset CaptureTime, string MetadataJson);
    private sealed record CaptureAiMetadata(bool Tracked, string Status, bool? AiSuccess, bool? VectorSuccess, bool? RetryQueued)
    {
        public static CaptureAiMetadata Untracked { get; } = new(false, "untracked", null, null, null);
    }
}
