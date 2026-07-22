using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddHttpClient("AuraWebhook", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<SimulatorState>();
builder.Services.AddSingleton<SimulatorWebhookSender>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "Aura generic media-analysis provider simulator", protocol_version = "1.0" }));
app.MapGet("/v1/capabilities", () => Results.Ok(new
{
    protocol_version = "1.0",
    capabilities = new[] { "image.sync", "video.async", "stream.subscription", "event.webhook", "event.replay", "embedding.inline", "artifact.presigned-url" },
    pipelines = new[] { new { code = "person-reid", models = new[] { "person-detect", "person-track", "reid" }, embedding_dimension = 512 } }
}));

app.MapPost("/v1/analysis/images", async (HttpRequest request, JsonElement payload, SimulatorState state) =>
{
    if (await Faults.TryWriteAsync(request, state)) return Results.StatusCode(state.FaultStatus);
    var id = ExternalId("image", payload);
    return Results.Ok(new
    {
        external_id = id,
        state = "completed",
        result = new { object_type = "person", confidence = 0.97, embedding = UnitVector(id), model_code = "sim-reid", model_version = "1.0" }
    });
});

app.MapPost("/v1/analysis/videos", async (HttpRequest request, JsonElement payload, SimulatorState state, SimulatorWebhookSender sender, CancellationToken ct) =>
{
    if (await Faults.TryWriteAsync(request, state)) return Results.StatusCode(state.FaultStatus);
    var id = ExternalId("video", payload);
    var job = state.Jobs.GetOrAdd(id, _ => new JobState(id, "accepted", 0, null, null, DateTimeOffset.UtcNow));
    _ = CompleteJobAsync(job.ExternalId, state, sender, CancellationToken.None);
    return Results.Accepted($"/v1/analysis/jobs/{Uri.EscapeDataString(id)}", new { external_id = id, state = job.State });
});

app.MapGet("/v1/analysis/jobs/{id}", async (string id, HttpRequest request, SimulatorState state) =>
{
    if (await Faults.TryWriteAsync(request, state)) return Results.StatusCode(state.FaultStatus);
    return state.Jobs.TryGetValue(id, out var job)
        ? Results.Ok(job)
        : Results.NotFound(new { error_code = "job_not_found", error_message = "Unknown simulator job." });
});

app.MapPost("/v1/analysis/jobs/{id}/cancel", (string id, SimulatorState state) =>
{
    state.Jobs.AddOrUpdate(id,
        _ => new JobState(id, "cancelled", 0, null, null, DateTimeOffset.UtcNow),
        (_, current) => current with { State = "cancelled", UpdatedAt = DateTimeOffset.UtcNow });
    return Results.Ok(new { external_id = id, state = "cancelled" });
});

app.MapPut("/v1/analysis/streams/{clientSubscriptionId}", async (
    string clientSubscriptionId,
    HttpRequest request,
    JsonElement payload,
    SimulatorState state,
    SimulatorWebhookSender sender,
    CancellationToken ct) =>
{
    if (await Faults.TryWriteAsync(request, state)) return Results.StatusCode(state.FaultStatus);
    var stream = new StreamState(clientSubscriptionId, "running", payload.Clone(), DateTimeOffset.UtcNow);
    state.Streams[clientSubscriptionId] = stream;
    await sender.SendStreamStateAsync(stream, "stream.started", ct);
    return Results.Ok(new { external_id = clientSubscriptionId, state = "running", progress = 100 });
});

app.MapGet("/v1/analysis/streams/{clientSubscriptionId}", (string clientSubscriptionId, SimulatorState state) =>
    state.Streams.TryGetValue(clientSubscriptionId, out var stream)
        ? Results.Ok(new { external_id = stream.Id, state = stream.State, progress = 100 })
        : Results.NotFound(new { error_code = "stream_not_found", error_message = "Unknown simulator stream." }));

app.MapDelete("/v1/analysis/streams/{clientSubscriptionId}", async (
    string clientSubscriptionId,
    SimulatorState state,
    SimulatorWebhookSender sender,
    CancellationToken ct) =>
{
    if (state.Streams.TryRemove(clientSubscriptionId, out var stream))
        await sender.SendStreamStateAsync(stream with { State = "stopped" }, "stream.stopped", ct);
    return Results.Ok(new { external_id = clientSubscriptionId, state = "stopped" });
});

app.MapPost("/v1/events/replay", async (ReplayRequest request, SimulatorState state, SimulatorWebhookSender sender, CancellationToken ct) =>
{
    var selected = state.Events.Values
        .Where(item => !request.From.HasValue || item.EventTime >= request.From.Value)
        .Where(item => !request.To.HasValue || item.EventTime <= request.To.Value)
        .OrderBy(item => item.EventTime)
        .Take(Math.Clamp(request.Limit, 1, 1000))
        .ToArray();
    foreach (var item in selected) await sender.SendAsync(item, ct);
    return Results.Ok(new { replayed = selected.Length });
});

app.MapPost("/admin/events", async (MediaEventEnvelope envelope, SimulatorState state, SimulatorWebhookSender sender, CancellationToken ct) =>
{
    var normalized = envelope with
    {
        SchemaVersion = string.IsNullOrWhiteSpace(envelope.SchemaVersion) ? "1.0" : envelope.SchemaVersion,
        EventId = string.IsNullOrWhiteSpace(envelope.EventId) ? $"sim-{Guid.NewGuid():N}" : envelope.EventId,
        ProviderCode = string.IsNullOrWhiteSpace(envelope.ProviderCode) ? state.ProviderCode : envelope.ProviderCode,
        TenantCode = string.IsNullOrWhiteSpace(envelope.TenantCode) ? state.TenantCode : envelope.TenantCode,
        EventTime = envelope.EventTime == default ? DateTimeOffset.UtcNow : envelope.EventTime,
        ProducedAt = envelope.ProducedAt ?? DateTimeOffset.UtcNow
    };
    state.Events[normalized.EventId] = normalized;
    var result = await sender.SendAsync(normalized, ct);
    return result.Success ? Results.Ok(new { emitted = normalized.EventId, result.StatusCode }) : Results.Problem(result.Error, statusCode: 502);
});

app.MapPut("/admin/fault", (FaultRequest request, SimulatorState state) =>
{
    state.FaultStatus = request.Status is >= 400 and <= 599 ? request.Status : 503;
    state.FaultDelay = TimeSpan.FromMilliseconds(Math.Clamp(request.DelayMilliseconds, 0, 120_000));
    state.FaultRemaining = Math.Clamp(request.Count, 0, 10_000);
    return Results.Ok(new { state.FaultStatus, delay_milliseconds = state.FaultDelay.TotalMilliseconds, state.FaultRemaining });
});

app.MapGet("/admin/state", (SimulatorState state) => Results.Ok(new
{
    jobs = state.Jobs.Values.OrderByDescending(item => item.UpdatedAt),
    streams = state.Streams.Values.OrderByDescending(item => item.UpdatedAt),
    event_count = state.Events.Count,
    fault_remaining = state.FaultRemaining
}));
app.MapGet("/admin/artifacts/{id}", (string id) => Results.File(
    Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
    "image/png",
    $"{id}.png"));
app.MapGet("/admin/artifact-redirect/{id}", (string id) =>
    Results.Redirect($"/admin/artifacts/{Uri.EscapeDataString(id)}", permanent: false, preserveMethod: true));

app.Run();

static string ExternalId(string prefix, JsonElement payload)
{
    var key = payload.TryGetProperty("idempotency_key", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
    if (string.IsNullOrWhiteSpace(key)) return $"{prefix}-{Guid.NewGuid():N}";
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    return $"{prefix}-{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
}

static float[] UnitVector(string seed)
{
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    var vector = new float[512];
    for (var index = 0; index < vector.Length; index++) vector[index] = (digest[index % digest.Length] + 1) / 256f;
    var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
    for (var index = 0; index < vector.Length; index++) vector[index] = (float)(vector[index] / norm);
    return vector;
}

static async Task CompleteJobAsync(string id, SimulatorState state, SimulatorWebhookSender sender, CancellationToken cancellationToken)
{
    await Task.Delay(state.JobCompletionDelay, cancellationToken);
    if (!state.Jobs.TryGetValue(id, out var current) || current.State == "cancelled") return;
    var completed = current with
    {
        State = "completed",
        Progress = 100,
        Result = JsonSerializer.SerializeToElement(new { object_count = 1, model_code = "sim-reid", model_version = "1.0" }),
        UpdatedAt = DateTimeOffset.UtcNow
    };
    state.Jobs[id] = completed;
    await sender.SendJobStateAsync(completed, "analysis.job.completed", cancellationToken);
}

internal sealed class SimulatorState(IConfiguration configuration)
{
    public ConcurrentDictionary<string, JobState> Jobs { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, StreamState> Streams { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, MediaEventEnvelope> Events { get; } = new(StringComparer.Ordinal);
    public string ProviderCode { get; } = configuration["Simulator:ProviderCode"] ?? "simulator";
    public string TenantCode { get; } = configuration["Simulator:TenantCode"] ?? "aura-default";
    public TimeSpan JobCompletionDelay { get; } = TimeSpan.FromMilliseconds(configuration.GetValue("Simulator:JobCompletionDelayMilliseconds", 500));
    public int FaultStatus { get; set; } = 503;
    public TimeSpan FaultDelay { get; set; }
    public int FaultRemaining;
}

internal sealed class SimulatorWebhookSender(IHttpClientFactory httpClientFactory, IConfiguration configuration, SimulatorState state)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<WebhookResult> SendJobStateAsync(JobState job, string eventType, CancellationToken cancellationToken) =>
        SendAsync(NewEnvelope(eventType, null, null, new
        {
            job_id = job.ExternalId,
            progress = job.Progress,
            result = job.Result,
            error_code = job.ErrorCode,
            error_message = job.ErrorMessage
        }), cancellationToken);

    public Task<WebhookResult> SendStreamStateAsync(StreamState stream, string eventType, CancellationToken cancellationToken)
    {
        var sourceId = stream.Configuration.TryGetProperty("source", out var source)
            && source.TryGetProperty("source_id", out var sourceValue) ? sourceValue.GetString() : null;
        return SendAsync(NewEnvelope(eventType, stream.Id, sourceId, new { state = stream.State }), cancellationToken);
    }

    public async Task<WebhookResult> SendAsync(MediaEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var callback = configuration["Simulator:AuraWebhookUrl"];
        var secret = configuration["Simulator:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(callback) || string.IsNullOrWhiteSpace(secret))
            return new WebhookResult(false, 0, "Simulator:AuraWebhookUrl or Simulator:WebhookSecret is not configured.");
        var body = JsonSerializer.Serialize(envelope, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var canonical = $"{timestamp}\n{nonce}\n{digest}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, callback)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Aura-Provider", state.ProviderCode);
        request.Headers.TryAddWithoutValidation("X-Aura-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Aura-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Aura-Signature", signature);
        try
        {
            using var response = await httpClientFactory.CreateClient("AuraWebhook").SendAsync(request, cancellationToken);
            var error = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(cancellationToken);
            return new WebhookResult(response.IsSuccessStatusCode, (int)response.StatusCode, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WebhookResult(false, 0, ex.Message);
        }
    }

    private MediaEventEnvelope NewEnvelope(string eventType, string? subscriptionId, string? sourceId, object payload)
    {
        var envelope = new MediaEventEnvelope(
            "1.0", $"sim-{Guid.NewGuid():N}", null, state.TenantCode, state.ProviderCode,
            subscriptionId, sourceId, null, eventType, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            System.Diagnostics.Activity.Current?.TraceId.ToString(), JsonSerializer.SerializeToElement(payload, JsonOptions));
        state.Events[envelope.EventId] = envelope;
        return envelope;
    }
}

internal static class Faults
{
    public static async Task<bool> TryWriteAsync(HttpRequest request, SimulatorState state)
    {
        if (state.FaultDelay > TimeSpan.Zero) await Task.Delay(state.FaultDelay, request.HttpContext.RequestAborted);
        while (Volatile.Read(ref state.FaultRemaining) > 0)
        {
            if (Interlocked.Decrement(ref state.FaultRemaining) >= 0) return true;
        }
        return false;
    }
}

internal sealed record JobState(string ExternalId, string State, decimal Progress, string? ErrorCode, string? ErrorMessage, DateTimeOffset UpdatedAt, JsonElement? Result = null);
internal sealed record StreamState(string Id, string State, JsonElement Configuration, DateTimeOffset UpdatedAt);
internal sealed record ReplayRequest(DateTimeOffset? From, DateTimeOffset? To, int Limit = 100);
internal sealed record FaultRequest(int Status, int Count, int DelayMilliseconds);
internal sealed record WebhookResult(bool Success, int StatusCode, string? Error);
internal sealed record MediaEventEnvelope(
    string SchemaVersion,
    string EventId,
    string? ProviderEventId,
    string TenantCode,
    string ProviderCode,
    string? SubscriptionId,
    string? SourceId,
    long? Sequence,
    string EventType,
    DateTimeOffset EventTime,
    DateTimeOffset? ProducedAt,
    string? TraceId,
    JsonElement Payload);

public partial class Program;
