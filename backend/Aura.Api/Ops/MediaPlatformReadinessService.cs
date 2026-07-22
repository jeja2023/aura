using Aura.Api.Data;
using Aura.Api.Graph;
using Dapper;
using Prometheus;

namespace Aura.Api.Ops;

internal sealed class MediaPlatformReadinessService(
    PgSqlConnectionFactory connectionFactory,
    IGraphRepository graph,
    IConfiguration configuration)
{
    private static readonly Gauge Backlog = Metrics.CreateGauge(
        "aura_media_backlog_messages",
        "Pending messages by durable queue and status.",
        new GaugeConfiguration { LabelNames = ["queue", "status"] });
    private static readonly Gauge OldestAge = Metrics.CreateGauge(
        "aura_media_backlog_oldest_age_seconds",
        "Age of the oldest pending durable message.",
        new GaugeConfiguration { LabelNames = ["queue"] });

    public async Task<MediaPlatformReadiness> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var pgvector = await connection.QuerySingleAsync<PgVectorReadiness>(new CommandDefinition(
            """
            SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname='vector') AS ExtensionAvailable,
              to_regclass('public.feature_embedding') IS NOT NULL AS TableAvailable,
              (SELECT COUNT(*) FROM feature_embedding) AS VectorCount
            """, cancellationToken: cancellationToken));
        var providers = await connection.QuerySingleAsync<ProviderReadiness>(new CommandDefinition(
            """
            SELECT COUNT(DISTINCT provider.provider_id) FILTER(WHERE provider.enabled) AS EnabledProviders,
              COUNT(*) FILTER(WHERE subscription.observed_state='running') AS RunningSubscriptions,
              COUNT(*) FILTER(WHERE subscription.observed_state IN ('degraded','failed')) AS DegradedSubscriptions,
              COUNT(*) FILTER(WHERE subscription.observed_state IN ('running','degraded')
                AND subscription.last_heartbeat_at<NOW()-(@HeartbeatTimeout * INTERVAL '1 second')) AS StaleSubscriptions
            FROM media_analysis_provider provider
            LEFT JOIN media_analysis_subscription subscription ON subscription.provider_id=provider.provider_id
            """,
            new { HeartbeatTimeout = Math.Clamp(configuration.GetValue("MediaAnalysis:Readiness:HeartbeatTimeoutSeconds", 120), 10, 86400) },
            cancellationToken: cancellationToken));

        var inbox = await QueueAsync(connection, true, cancellationToken);
        var outbox = await QueueAsync(connection, false, cancellationToken);
        ObserveQueue("inbox", inbox);
        ObserveQueue("outbox", outbox);

        var workerRows = (await connection.QueryAsync<WorkerReadinessRow>(new CommandDefinition(
            """
            SELECT DISTINCT ON(worker_name) worker_name AS WorkerName,instance_id AS InstanceId,status AS Status,
              last_success_at AS LastSuccessAt,last_error_at AS LastErrorAt,last_error AS LastError,updated_at AS UpdatedAt
            FROM background_worker_heartbeat ORDER BY worker_name,updated_at DESC
            """, cancellationToken: cancellationToken))).ToDictionary(item => item.WorkerName, StringComparer.Ordinal);
        var staleSeconds = Math.Clamp(configuration.GetValue("MediaAnalysis:Readiness:WorkerStaleSeconds", 30), 5, 3600);
        var now = DateTime.UtcNow;
        var workers = ExpectedWorkers().Select(name =>
        {
            workerRows.TryGetValue(name, out var row);
            var age = row?.LastSuccessAt is { } last ? Math.Max(0, (now - NormalizeUtc(last)).TotalSeconds) : (double?)null;
            return new WorkerReadiness(name, row?.InstanceId, row?.Status ?? "missing", row?.LastSuccessAt,
                age, row?.LastError, age.HasValue && age.Value <= staleSeconds && row?.Status == "running");
        }).ToArray();

        var graphEnabled = configuration.GetValue("Graph:Enabled", false);
        var graphHealth = graphEnabled
            ? await graph.GetHealthAsync(cancellationToken)
            : new GraphHealth(false, configuration["Graph:Arango:Database"] ?? "aura_graph",
                configuration["Graph:Arango:GraphName"] ?? "aura_domain", "disabled", null);
        var vectorRequired = !string.Equals(configuration["VectorIndex:ReadEngine"], "legacy-arangodb", StringComparison.OrdinalIgnoreCase);
        var ready = (!vectorRequired || (pgvector.ExtensionAvailable && pgvector.TableAvailable))
            && workers.All(item => item.Healthy)
            && (!graphEnabled || graphHealth.Available);
        return new MediaPlatformReadiness(ready, pgvector, providers, inbox, outbox, workers,
            new GraphComponentReadiness(graphEnabled, graphHealth.Available, graphHealth.Database,
                graphHealth.Graph, graphHealth.Version, graphHealth.Error));
    }

    private static async Task<QueueReadiness> QueueAsync(
        System.Data.IDbConnection connection,
        bool inbox,
        CancellationToken cancellationToken)
    {
        var sql = inbox
            ? "SELECT status AS Status,COUNT(*) AS Count,MIN(COALESCE(next_attempt_at,received_at)) AS OldestAt FROM media_analysis_inbox GROUP BY status"
            : "SELECT status AS Status,COUNT(*) AS Count,MIN(COALESCE(available_at,created_at)) AS OldestAt FROM integration_outbox GROUP BY status";
        var rows = (await connection.QueryAsync<QueueStatusRow>(new CommandDefinition(sql,
            cancellationToken: cancellationToken))).AsList();
        var counts = rows.ToDictionary(item => item.Status, item => item.Count, StringComparer.Ordinal);
        var activeStatuses = inbox
            ? new HashSet<string>(["received", "processing", "retry_wait", "dead_letter"], StringComparer.Ordinal)
            : new HashSet<string>(["pending", "processing", "retry_wait", "dead_letter"], StringComparer.Ordinal);
        var active = rows.Where(item => activeStatuses.Contains(item.Status)).ToArray();
        var oldestValues = active.Where(item => item.OldestAt.HasValue).Select(item => NormalizeUtc(item.OldestAt!.Value)).ToArray();
        var oldestAge = oldestValues.Length == 0 ? 0 : Math.Max(0, (DateTime.UtcNow - oldestValues.Min()).TotalSeconds);
        return new QueueReadiness(counts, active.Sum(item => item.Count), oldestAge,
            counts.GetValueOrDefault("dead_letter"),
            counts.GetValueOrDefault(inbox ? "received" : "pending"));
    }

    private static void ObserveQueue(string queue, QueueReadiness readiness)
    {
        foreach (var item in readiness.Counts) Backlog.WithLabels(queue, item.Key).Set(item.Value);
        OldestAge.WithLabels(queue).Set(readiness.OldestAgeSeconds);
    }

    private string[] ExpectedWorkers()
    {
        var result = new List<string>();
        if (configuration.GetValue("MediaAnalysis:Workers:Enabled", false))
        {
            result.AddRange(["media-analysis-job", "media-analysis-job-monitor", "media-analysis-subscription", "media-analysis-inbox"]);
            if (configuration.GetValue("VectorIndex:Compensation:Enabled", true)) result.Add("vector-write-compensation");
            if (configuration.GetValue("MediaAnalysis:Artifacts:Enabled", true)) result.Add("media-artifact-archive");
        }
        if (configuration.GetValue("Graph:Enabled", false)) result.AddRange(["graph-projection", "graph-rebuild"]);
        return result.ToArray();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed record QueueStatusRow(string Status, long Count, DateTime? OldestAt);
    private sealed record WorkerReadinessRow(string WorkerName, string InstanceId, string Status,
        DateTime? LastSuccessAt, DateTime? LastErrorAt, string? LastError, DateTime UpdatedAt);
}

internal sealed record MediaPlatformReadiness(
    bool Ready,
    PgVectorReadiness Pgvector,
    ProviderReadiness Providers,
    QueueReadiness Inbox,
    QueueReadiness Outbox,
    IReadOnlyList<WorkerReadiness> Workers,
    GraphComponentReadiness Graph);
internal sealed record PgVectorReadiness(bool ExtensionAvailable, bool TableAvailable, long VectorCount);
internal sealed record ProviderReadiness(long EnabledProviders, long RunningSubscriptions, long DegradedSubscriptions, long StaleSubscriptions);
internal sealed record QueueReadiness(IReadOnlyDictionary<string, long> Counts, long ActiveCount, double OldestAgeSeconds, long DeadLetterCount, long PendingCount);
internal sealed record WorkerReadiness(string WorkerName, string? InstanceId, string Status, DateTime? LastSuccessAt, double? LastSuccessAgeSeconds, string? LastError, bool Healthy);
internal sealed record GraphComponentReadiness(bool Enabled, bool Available, string Database, string Graph, string Version, string? Error);
