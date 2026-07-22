using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aura.Api.Graph;

internal sealed class ArangoGraphRepository(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ArangoGraphRepository> logger) : IGraphRepository
{
    private static readonly string[] VertexCollections =
        ["campuses", "buildings", "floors", "rooms", "cameras", "rois", "persons", "analysis_sources"];
    private static readonly string[] EdgeCollections =
        ["contains", "located_in", "covers", "connects", "visited", "co_occurs", "transition"];
    private static readonly GraphEdgeDefinition[] EdgeDefinitions =
    [
        new("contains", ["campuses", "buildings", "floors", "rooms"], ["buildings", "floors", "rooms"]),
        new("located_in", ["cameras", "rois", "analysis_sources", "floors"], ["campuses", "buildings", "floors", "rooms", "cameras"]),
        new("covers", ["cameras"], ["rois", "rooms"]),
        new("connects", ["cameras"], ["cameras"]),
        new("visited", ["persons"], ["cameras", "rooms", "analysis_sources"]),
        new("co_occurs", ["persons"], ["persons"]),
        new("transition", ["cameras"], ["cameras"])
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    private string Database => configuration["Graph:Arango:Database"]?.Trim() ?? "aura_graph";
    private string GraphName => configuration["Graph:Arango:GraphName"]?.Trim() ?? "aura_domain";
    private string BaseUrl => (configuration["Graph:Arango:BaseUrl"]?.Trim() ?? "http://127.0.0.1:8529").TrimEnd('/');

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            foreach (var collection in VertexCollections)
            {
                await EnsureCollectionAsync(collection, isEdge: false, cancellationToken);
            }
            foreach (var collection in EdgeCollections)
            {
                await EnsureCollectionAsync(collection, isEdge: true, cancellationToken);
            }
            await EnsureGraphAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task ProjectEventAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var tenantId = Long(payload, "tenant_id") ?? throw new InvalidDataException("Graph event has no tenant_id.");
        var sourceId = Long(payload, "source_id");
        var entityId = String(payload, "entity_id");
        var eventTime = Date(payload, "event_time") ?? DateTimeOffset.UtcNow;
        if (sourceId.HasValue)
        {
            await UpsertVertexAsync("analysis_sources", GraphKeys.Source(tenantId, sourceId.Value), new
            {
                tenant_id = tenantId,
                source_id = sourceId.Value,
                source_version = String(payload, "schema_version") ?? "1",
                updated_at = DateTimeOffset.UtcNow
            }, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            var personKey = GraphKeys.Person(tenantId, entityId);
            await UpsertVertexAsync("persons", personKey, new
            {
                tenant_id = tenantId,
                person_id = entityId,
                source_version = String(payload, "schema_version") ?? "1",
                last_seen = eventTime,
                updated_at = DateTimeOffset.UtcNow
            }, cancellationToken);
            if (sourceId.HasValue)
            {
                var bucket = new DateTimeOffset(eventTime.Year, eventTime.Month, eventTime.Day, eventTime.Hour, 0, 0, eventTime.Offset);
                var from = $"persons/{personKey}";
                var to = GraphKeys.SourceRef(tenantId, sourceId.Value);
                var edgeKey = StableEdgeKey(tenantId, "visited", from, to, bucket.ToUniversalTime().ToString("O"));
                await UpsertEdgeAsync("visited", edgeKey, from, to, new
                {
                    tenant_id = tenantId,
                    bucket_start = bucket,
                    count = 1,
                    first_seen = eventTime,
                    last_seen = eventTime,
                    source_version = String(payload, "schema_version") ?? "1",
                    updated_at = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        foreach (var collection in EdgeCollections.Concat(VertexCollections))
        {
            _ = await QueryAsync(
                "reset",
                "FOR document IN @@collection REMOVE document IN @@collection OPTIONS { ignoreErrors: true }",
                new Dictionary<string, object?> { ["@collection"] = collection }, cancellationToken);
        }
    }

    public async Task UpsertVertexAsync(string collection, string key, object document, CancellationToken cancellationToken)
    {
        ValidateCollection(collection, VertexCollections);
        var payload = MergeDocument(document, new Dictionary<string, object?> { ["_key"] = SafeKey(key) });
        await SendJsonAsync(HttpMethod.Post, $"/_api/document/{collection}?overwriteMode=update&mergeObjects=true", payload, cancellationToken);
    }

    public async Task UpsertEdgeAsync(string collection, string key, string from, string to, object document, CancellationToken cancellationToken)
    {
        ValidateCollection(collection, EdgeCollections);
        var payload = MergeDocument(document, new Dictionary<string, object?>
        {
            ["_key"] = SafeKey(key),
            ["_from"] = from,
            ["_to"] = to
        });
        await SendJsonAsync(HttpMethod.Post, $"/_api/document/{collection}?overwriteMode=update&mergeObjects=true", payload, cancellationToken);
    }

    public async Task<JsonElement> QueryAsync(string operation, string aql, object bindVars, CancellationToken cancellationToken)
    {
        using var metric = GraphMetrics.TrackQuery(operation);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var result = await SendJsonAsync(HttpMethod.Post, "/_api/cursor", new
            {
                query = aql,
                bindVars,
                options = new { maxRuntime = Math.Max(1, configuration.GetValue("Graph:Arango:QueryMaxRuntimeSeconds", 15)) },
                batchSize = 1000,
                ttl = 30
            }, cancellationToken);
            if (!result.TryGetProperty("result", out var rows))
            {
                metric.Success(0);
                return JsonSerializer.SerializeToElement(Array.Empty<object>());
            }
            metric.Success(rows.GetArrayLength());
            return rows.Clone();
        }
        catch (OperationCanceledException)
        {
            metric.Timeout();
            throw;
        }
    }

    public async Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var version = await SendJsonAsync(HttpMethod.Get, "/_api/version", null, cancellationToken);
            return new GraphHealth(true, Database, GraphName, String(version, "version") ?? "unknown", null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ArangoDB graph health check failed.");
            return new GraphHealth(false, Database, GraphName, "unknown", ex.Message);
        }
    }

    private async Task EnsureCollectionAsync(string name, bool isEdge, CancellationToken cancellationToken)
    {
        try
        {
            _ = await SendJsonAsync(HttpMethod.Get, $"/_api/collection/{name}", null, cancellationToken);
        }
        catch (ArangoHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _ = await SendJsonAsync(HttpMethod.Post, "/_api/collection", new { name, type = isEdge ? 3 : 2 }, cancellationToken);
        }
    }

    private async Task EnsureGraphAsync(CancellationToken cancellationToken)
    {
        JsonElement existing;
        try
        {
            existing = await SendJsonAsync(HttpMethod.Get, $"/_api/gharial/{GraphName}", null, cancellationToken);
        }
        catch (ArangoHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _ = await SendJsonAsync(HttpMethod.Post, "/_api/gharial", new
            {
                name = GraphName,
                edgeDefinitions = EdgeDefinitions,
                orphanCollections = Array.Empty<string>()
            }, cancellationToken);
            return;
        }

        if (!existing.TryGetProperty("graph", out var graphDocument)) return;
        var existingEdges = graphDocument.TryGetProperty("edgeDefinitions", out var edgeDefinitions)
            ? edgeDefinitions.EnumerateArray()
                .Select(item => item.TryGetProperty("collection", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var desiredVertexCollections = EdgeDefinitions.SelectMany(item => item.From.Concat(item.To)).ToHashSet(StringComparer.Ordinal);
        if (graphDocument.TryGetProperty("orphanCollections", out var orphanCollections))
        {
            foreach (var orphan in orphanCollections.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null))
            {
                if (desiredVertexCollections.Contains(orphan!))
                    _ = await SendJsonAsync(HttpMethod.Delete,
                        $"/_api/gharial/{Uri.EscapeDataString(GraphName)}/vertex/{Uri.EscapeDataString(orphan!)}?dropCollection=false",
                        null, cancellationToken);
            }
        }

        foreach (var definition in EdgeDefinitions)
        {
            var method = existingEdges.Contains(definition.Collection) ? HttpMethod.Put : HttpMethod.Post;
            var path = method == HttpMethod.Put
                ? $"/_api/gharial/{Uri.EscapeDataString(GraphName)}/edge/{Uri.EscapeDataString(definition.Collection)}"
                : $"/_api/gharial/{Uri.EscapeDataString(GraphName)}/edge";
            _ = await SendJsonAsync(method, path, definition, cancellationToken);
        }
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{BaseUrl}/_db/{Uri.EscapeDataString(Database)}{path}");
        var user = configuration["Graph:Arango:Username"] ?? "aura_graph";
        var password = configuration["Graph:Arango:Password"] ?? string.Empty;
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await httpClientFactory.CreateClient("ArangoGraph").SendAsync(request, cancellationToken);
        var text = await ReadResponseAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ArangoHttpException(response.StatusCode, $"ArangoDB returned HTTP {(int)response.StatusCode}: {Trim(text, 500)}");
        }
        return string.IsNullOrWhiteSpace(text)
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
    }

    private static Dictionary<string, object?> MergeDocument(object source, Dictionary<string, object?> target)
    {
        var element = JsonSerializer.SerializeToElement(source, JsonOptions);
        foreach (var property in element.EnumerateObject()) target[property.Name] = property.Value.Clone();
        return target;
    }

    internal static string SafeKey(string value)
    {
        var builder = new StringBuilder(Math.Min(254, value.Length));
        foreach (var character in value)
        {
            if (builder.Length >= 254) break;
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':' or '.' or '@' or '(' or ')' or '+' or ',' or '=' or ';' or '$' or '!' or '*' or '\'' or '%' ? character : '_');
        }
        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    internal static string StableEdgeKey(long tenantId, string relation, string from, string to, string bucket = "")
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}|{relation}|{from}|{to}|{bucket}"));
        return $"t{tenantId}_{relation}_{Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static void ValidateCollection(string value, IReadOnlyCollection<string> allowList)
    {
        if (!allowList.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Unsupported graph collection.");
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;
    private static string Trim(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Clamp(configuration.GetValue("Graph:Arango:MaxResponseBytes", 16 * 1024 * 1024), 1024, 64 * 1024 * 1024);
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("ArangoDB response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("ArangoDB response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private sealed record GraphEdgeDefinition(string Collection, string[] From, string[] To);

    private sealed class ArangoHttpException(HttpStatusCode statusCode, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
