using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aura.Api.Ai;
using Aura.Api.Data;
using Aura.Api.MediaAnalysis;
using Dapper;
using Npgsql;

namespace Aura.Api.Vector;

internal sealed class LegacyArangoVectorExportClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public async Task<IReadOnlyList<LegacyVectorExportRow>> ReadPageAsync(
        string? afterKey,
        int limit,
        CancellationToken cancellationToken)
    {
        var baseUrl = configuration["VectorIndex:LegacyArango:BaseUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("VectorIndex:LegacyArango:BaseUrl is not configured.");
        _ = MediaAnalysisOutboundUrlPolicy.ValidateSyntax(baseUrl);
        var database = configuration["VectorIndex:LegacyArango:Database"]?.Trim() ?? "aura";
        var collection = configuration["VectorIndex:LegacyArango:Collection"]?.Trim() ?? "person_features";
        if (!SafeIdentifier(collection)) throw new InvalidDataException("Legacy Arango collection name is invalid.");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/_db/{Uri.EscapeDataString(database)}/_api/cursor");
        var user = configuration["VectorIndex:LegacyArango:Username"] ?? string.Empty;
        var password = configuration["VectorIndex:LegacyArango:Password"] ?? string.Empty;
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        request.Content = JsonContent.Create(new
        {
            query = """
                FOR item IN @@collection
                  FILTER @afterKey == null OR item._key > @afterKey
                  FILTER HAS(item,'vid') AND HAS(item,'feature')
                  SORT item._key
                  LIMIT @limit
                  RETURN { key: item._key, vid: item.vid, feature: item.feature, metadata: item.metadata }
                """,
            bindVars = new Dictionary<string, object?>
            {
                ["@collection"] = collection,
                ["afterKey"] = string.IsNullOrWhiteSpace(afterKey) ? null : afterKey,
                ["limit"] = Math.Clamp(limit, 1, 5000)
            }
        }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var response = await httpClientFactory.CreateClient("LegacyArangoVector").SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Legacy Arango export returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("result", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<LegacyVectorExportRow>();
        foreach (var row in rows.EnumerateArray())
        {
            var key = row.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
            var vid = row.TryGetProperty("vid", out var vidValue) ? vidValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(vid)
                || !row.TryGetProperty("feature", out var feature) || feature.ValueKind != JsonValueKind.Array) continue;
            var metadata = row.TryGetProperty("metadata", out var metadataValue)
                ? metadataValue.GetRawText()
                : "{}";
            result.Add(new LegacyVectorExportRow(key, vid,
                feature.EnumerateArray().Select(item => item.GetSingle()).ToArray(), metadata));
        }
        return result;
    }

    private static bool SafeIdentifier(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static string Trim(string value, int length) => value.Length <= length ? value : value[..length];
}

internal sealed class VectorMigrationService(
    PgSqlConnectionFactory connectionFactory,
    PgVectorIndex pgVector,
    LegacyArangoVectorExportClient exporter,
    LegacyArangoVectorIndex legacyIndex)
{
    public async Task<VectorMigrationResult> BackfillAsync(VectorBackfillRequest request, CancellationToken cancellationToken)
    {
        ValidateBackfill(request);
        var totalScanned = 0;
        var totalMigrated = 0;
        var totalSkipped = 0;
        var totalFailed = 0;
        var completed = false;
        var maxBatches = Math.Clamp(request.MaxBatches, 1, 1000);
        for (var batchNumber = 0; batchNumber < maxBatches && !completed; batchNumber++)
        {
            var checkpoint = await BeginOrReadCheckpointAsync(
                request.MigrationName,
                ShouldRestartCheckpoint(request.Restart, batchNumber),
                cancellationToken);
            var page = await exporter.ReadPageAsync(checkpoint.LastSourceKey, request.BatchSize, cancellationToken);
            if (page.Count == 0)
            {
                completed = true;
                await CompleteCheckpointAsync(request.MigrationName, cancellationToken);
                break;
            }

            foreach (var row in page)
            {
                totalScanned++;
                var migratedDelta = 0;
                var skippedDelta = 0;
                var failedDelta = 0;
                try
                {
                    var normalized = VectorValidation.Normalize(row.Feature);
                    await pgVector.UpsertAsync(new VectorIndexDocument(
                        request.TenantId, request.ModelId, row.Vid, null, row.Key, normalized, row.MetadataJson),
                        cancellationToken);
                    totalMigrated++;
                    migratedDelta = 1;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or JsonException or FormatException)
                {
                    totalSkipped++;
                    skippedDelta = 1;
                }
                catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
                {
                    totalFailed++;
                    failedDelta = 1;
                }
                await AdvanceCheckpointAsync(request.MigrationName, row.Key, migratedDelta,
                    skippedDelta, failedDelta, cancellationToken);
            }
            completed = page.Count < Math.Clamp(request.BatchSize, 1, 5000);
            if (completed) await CompleteCheckpointAsync(request.MigrationName, cancellationToken);
        }
        return new VectorMigrationResult(request.MigrationName, totalScanned, totalMigrated, totalSkipped, totalFailed, completed);
    }

    internal static bool ShouldRestartCheckpoint(bool restartRequested, int batchNumber) =>
        restartRequested && batchNumber == 0;

    public async Task<VectorShadowEvaluationResult> EvaluateAsync(
        VectorShadowEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var sampleLimit = Math.Clamp(request.SampleCount, 1, 1000);
        var topK = Math.Clamp(request.TopK, 1, 50);
        await using var connection = connectionFactory.CreateConnection();
        var samples = (await connection.QueryAsync<VectorEvaluationSample>(new CommandDefinition(
            """
            SELECT embedding_id AS EmbeddingId,vid AS Vid,feature::text AS FeatureText
            FROM feature_embedding WHERE tenant_id=@TenantId AND model_id=@ModelId
            ORDER BY embedding_id LIMIT @Limit
            """,
            new { request.TenantId, request.ModelId, Limit = sampleLimit },
            cancellationToken: cancellationToken))).AsList();
        if (samples.Count == 0) throw new InvalidOperationException("No pgvector samples are available for evaluation.");

        var recalls = new List<double>();
        var reciprocalRanks = new List<double>();
        var overlaps = new List<double>();
        var pgLatencies = new List<double>();
        var legacyLatencies = new List<double>();
        var empty = 0;
        var items = new List<object>();
        foreach (var sample in samples)
        {
            var feature = VectorText.Parse(sample.FeatureText);
            var timer = Stopwatch.StartNew();
            var pgHits = await pgVector.SearchAsync(new VectorIndexQuery(
                request.TenantId, request.ModelId, feature, topK), cancellationToken);
            timer.Stop();
            pgLatencies.Add(timer.Elapsed.TotalMilliseconds);

            timer.Restart();
            var legacyHits = await legacyIndex.SearchAsync(new VectorIndexQuery(
                request.TenantId, request.ModelId, feature, topK), cancellationToken);
            timer.Stop();
            legacyLatencies.Add(timer.Elapsed.TotalMilliseconds);

            var pgVids = pgHits.Select(item => item.Vid).ToArray();
            var legacyVids = legacyHits.Select(item => item.Vid).ToArray();
            var legacySet = legacyVids.ToHashSet(StringComparer.Ordinal);
            var intersection = pgVids.Count(legacySet.Contains);
            var recall = legacySet.Count == 0 ? (pgVids.Length == 0 ? 1d : 0d) : intersection / (double)legacySet.Count;
            var union = pgVids.Union(legacyVids, StringComparer.Ordinal).Count();
            var overlap = union == 0 ? 1d : intersection / (double)union;
            var rank = legacyVids.Length == 0 ? 0 : Array.FindIndex(pgVids, value => value == legacyVids[0]) + 1;
            var reciprocalRank = rank > 0 ? 1d / rank : 0d;
            if (pgVids.Length == 0) empty++;
            recalls.Add(recall);
            overlaps.Add(overlap);
            reciprocalRanks.Add(reciprocalRank);
            items.Add(new
            {
                sample.EmbeddingId,
                sample.Vid,
                pg_vids = pgVids,
                legacy_vids = legacyVids,
                recall_at_k = recall,
                reciprocal_rank = reciprocalRank,
                topk_overlap = overlap
            });
        }

        var result = new VectorShadowEvaluationResult(
            samples.Count,
            topK,
            recalls.Average(),
            reciprocalRanks.Average(),
            overlaps.Average(),
            empty / (double)samples.Count,
            Percentile95(pgLatencies),
            Percentile95(legacyLatencies));
        var report = JsonSerializer.Serialize(new { summary = result, items }, MediaAnalysisJson.Options);
        var evaluationId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO vector_shadow_evaluation(tenant_id,model_id,sample_count,top_k,recall_at_k,mrr,
              topk_overlap,empty_result_rate,pgvector_p95_ms,legacy_p95_ms,report_json)
            VALUES(@TenantId,@ModelId,@SampleCount,@TopK,@RecallAtK,@Mrr,@TopKOverlap,@EmptyResultRate,
              @PgVectorP95Ms,@LegacyP95Ms,CAST(@Report AS jsonb)) RETURNING evaluation_id
            """,
            new
            {
                request.TenantId,
                request.ModelId,
                result.SampleCount,
                result.TopK,
                result.RecallAtK,
                result.Mrr,
                result.TopKOverlap,
                result.EmptyResultRate,
                result.PgVectorP95Ms,
                result.LegacyP95Ms,
                Report = report
            }, cancellationToken: cancellationToken));
        return result with { EvaluationId = evaluationId };
    }

    public async Task<object> GetMigrationStatusAsync(string? migrationName, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var checkpoints = (await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT migration_name AS MigrationName,source_engine AS SourceEngine,target_engine AS TargetEngine,
              last_source_key AS LastSourceKey,scanned_count AS ScannedCount,migrated_count AS MigratedCount,
              skipped_count AS SkippedCount,failed_count AS FailedCount,status AS Status,last_error AS LastError,
              started_at AS StartedAt,completed_at AS CompletedAt,updated_at AS UpdatedAt
            FROM vector_migration_checkpoint
            WHERE @MigrationName IS NULL OR migration_name=@MigrationName ORDER BY updated_at DESC
            """,
            new { MigrationName = string.IsNullOrWhiteSpace(migrationName) ? null : migrationName.Trim() },
            cancellationToken: cancellationToken))).AsList();
        var evaluations = (await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT evaluation_id AS EvaluationId,tenant_id AS TenantId,model_id AS ModelId,sample_count AS SampleCount,
              top_k AS TopK,recall_at_k AS RecallAtK,mrr AS Mrr,topk_overlap AS TopKOverlap,
              empty_result_rate AS EmptyResultRate,pgvector_p95_ms AS PgvectorP95Ms,
              legacy_p95_ms AS LegacyP95Ms,created_at AS CreatedAt
            FROM vector_shadow_evaluation ORDER BY evaluation_id DESC LIMIT 50
            """, cancellationToken: cancellationToken))).AsList();
        var compensation = (await connection.QueryAsync<dynamic>(new CommandDefinition(
            "SELECT status AS Status,COUNT(*) AS Count,MIN(created_at) AS OldestAt FROM vector_write_compensation GROUP BY status",
            cancellationToken: cancellationToken))).AsList();
        return new { checkpoints, evaluations, compensation };
    }

    private async Task<VectorMigrationCheckpoint> BeginOrReadCheckpointAsync(
        string migrationName,
        bool restart,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (restart)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM vector_migration_checkpoint WHERE migration_name=@MigrationName",
                new { MigrationName = migrationName }, cancellationToken: cancellationToken));
        }
        return await connection.QuerySingleAsync<VectorMigrationCheckpoint>(new CommandDefinition(
            """
            INSERT INTO vector_migration_checkpoint(migration_name,source_engine,target_engine,status,started_at)
            VALUES(@MigrationName,'legacy-arangodb','pgvector','running',NOW())
            ON CONFLICT(migration_name) DO UPDATE SET status='running',last_error=NULL,
              started_at=COALESCE(vector_migration_checkpoint.started_at,NOW()),updated_at=NOW()
            RETURNING migration_name AS MigrationName,last_source_key AS LastSourceKey
            """,
            new { MigrationName = migrationName }, cancellationToken: cancellationToken));
    }

    private async Task AdvanceCheckpointAsync(
        string migrationName,
        string key,
        int migrated,
        int skipped,
        int failed,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE vector_migration_checkpoint SET last_source_key=@Key,scanned_count=scanned_count+1,
              migrated_count=migrated_count+@Migrated,skipped_count=skipped_count+@Skipped,
              failed_count=failed_count+@Failed,updated_at=NOW() WHERE migration_name=@MigrationName
            """,
            new { MigrationName = migrationName, Key = key, Migrated = migrated, Skipped = skipped, Failed = failed },
            cancellationToken: cancellationToken));
    }

    private async Task CompleteCheckpointAsync(string migrationName, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vector_migration_checkpoint SET status='completed',completed_at=NOW(),updated_at=NOW() WHERE migration_name=@MigrationName",
            new { MigrationName = migrationName }, cancellationToken: cancellationToken));
    }

    private static void ValidateBackfill(VectorBackfillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MigrationName) || request.MigrationName.Length > 128)
            throw new ArgumentException("migrationName is required and must not exceed 128 characters.");
        if (request.TenantId <= 0 || request.ModelId <= 0)
            throw new ArgumentException("tenantId and modelId are required.");
    }

    private static double Percentile95(List<double> values)
    {
        values.Sort();
        return values[Math.Max(0, (int)Math.Ceiling(values.Count * 0.95) - 1)];
    }

    private sealed record VectorMigrationCheckpoint(string MigrationName, string? LastSourceKey);
    private sealed record VectorEvaluationSample(long EmbeddingId, string Vid, string FeatureText);
}

internal sealed record LegacyVectorExportRow(string Key, string Vid, IReadOnlyList<float> Feature, string MetadataJson);
internal sealed record VectorBackfillRequest(string MigrationName, long TenantId, long ModelId, int BatchSize = 500, int MaxBatches = 10, bool Restart = false);
internal sealed record VectorMigrationResult(string MigrationName, int Scanned, int Migrated, int Skipped, int Failed, bool Completed);
internal sealed record VectorShadowEvaluationRequest(long TenantId, long ModelId, int SampleCount = 100, int TopK = 10);
internal sealed record VectorShadowEvaluationResult(
    int SampleCount,
    int TopK,
    double RecallAtK,
    double Mrr,
    double TopKOverlap,
    double EmptyResultRate,
    double PgVectorP95Ms,
    double LegacyP95Ms,
    long? EvaluationId = null);
