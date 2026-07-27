using System.Data;
using System.Text.Json;
using Aura.Api.Cache;
using Aura.Api.Data;
using Aura.Api.Graph;
using Aura.Api.Internal;
using Dapper;

namespace Aura.Api.Product;

internal sealed class DataDeletionProjectionService(
    PgSqlConnectionFactory connectionFactory,
    RedisConnectionProvider redis,
    IGraphRepository graph,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DataDeletionProjectionService> logger)
{
    private static readonly string[] Stores =
        ["postgres", "pgvector", "arangodb", "redis", "object_storage", "exports", "backup"];
    private static readonly string[] GraphCollections =
        ["contains", "located_in", "covers", "connects", "visited", "co_occurs", "transition",
         "campuses", "buildings", "floors", "rooms", "cameras", "rois", "persons", "analysis_sources"];
    private static readonly IReadOnlyDictionary<string, (string Table, string IdColumn, string? TenantColumn)> AuthorityTables =
        new Dictionary<string, (string, string, string?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard_event"] = ("business_event", "event_id", "tenant_id"),
            ["inbox"] = ("media_analysis_inbox", "inbox_id", "tenant_id"),
            ["outbox"] = ("integration_outbox", "outbox_id", "tenant_id"),
            ["capture"] = ("capture_record", "capture_id", "tenant_id"),
            ["alert"] = ("alert_record", "alert_id", "tenant_id"),
            ["case_activity"] = ("incident_case_activity", "activity_id", "tenant_id"),
            ["audit"] = ("log_operation", "op_id", null),
            ["ai_evaluation"] = ("ai_evaluation_run", "evaluation_run_id", "tenant_id")
        };
    private readonly string storageRoot = Path.GetFullPath(ProjectPaths.ResolveStorageRoot(environment));
    private readonly string exportRoot = Path.GetFullPath(Path.Combine(ProjectPaths.ResolveStorageRoot(environment), "evidence-exports"));

    public async Task<IReadOnlyList<DataDeletionDeliveryView>> ListAsync(
        long cleanupJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<DataDeletionDeliveryView>(new CommandDefinition(
            """
            SELECT d.delivery_id AS DeliveryId,t.deletion_tombstone_id AS DeletionTombstoneId,
              t.tenant_id AS TenantId,t.cleanup_job_id AS CleanupJobId,t.data_type AS DataType,
              t.object_id AS ObjectId,d.store_type AS StoreType,d.status AS Status,d.attempts AS Attempts,
              d.last_error AS LastError,d.proof_json::text AS ProofJson,d.completed_at AS CompletedAt,
              d.available_at AS AvailableAt,d.updated_at AS UpdatedAt
            FROM data_deletion_delivery d
            JOIN data_deletion_tombstone t ON t.deletion_tombstone_id=d.deletion_tombstone_id
            WHERE t.cleanup_job_id=@CleanupJobId ORDER BY t.deletion_tombstone_id,d.delivery_id
            """, new { CleanupJobId = cleanupJobId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<ProductCommandResult> ReplayAsync(
        long cleanupJobId,
        string? storeType,
        CancellationToken cancellationToken)
    {
        var store = string.IsNullOrWhiteSpace(storeType) ? null : storeType.Trim().ToLowerInvariant();
        if (store is not null && !Stores.Contains(store, StringComparer.Ordinal))
            return new(ProductCommandStatus.Invalid, Message: "Unsupported deletion store type");
        await using var connection = connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE data_deletion_delivery d SET status='pending',attempts=0,available_at=CURRENT_TIMESTAMP,
              locked_by=NULL,lease_until=NULL,last_error=NULL,completed_at=NULL,updated_at=CURRENT_TIMESTAMP
            FROM data_deletion_tombstone t
            WHERE d.deletion_tombstone_id=t.deletion_tombstone_id AND t.cleanup_job_id=@CleanupJobId
              AND (@StoreType IS NULL OR d.store_type=@StoreType) AND d.status IN ('failed','blocked')
            """, new { CleanupJobId = cleanupJobId, StoreType = store }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { cleanupJobId, storeType = store, replayed = affected });
    }

    internal async Task<DataDeletionDeliveryWork?> ClaimAsync(CancellationToken cancellationToken)
    {
        await SeedAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<DataDeletionDeliveryWork>(new CommandDefinition(
            """
            WITH due AS (
              SELECT delivery_id FROM data_deletion_delivery
              WHERE (status IN ('pending','failed') OR (status='running' AND lease_until<CURRENT_TIMESTAMP))
                AND available_at<=CURRENT_TIMESTAMP
              ORDER BY delivery_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE data_deletion_delivery d SET status='running',attempts=attempts+1,locked_by=@Worker,
              lease_until=CURRENT_TIMESTAMP+INTERVAL '5 minutes',updated_at=CURRENT_TIMESTAMP
            FROM due, data_deletion_tombstone t, data_archive_record a
            WHERE d.delivery_id=due.delivery_id AND t.deletion_tombstone_id=d.deletion_tombstone_id
              AND a.archive_record_id=t.archive_record_id
            RETURNING d.delivery_id AS DeliveryId,t.deletion_tombstone_id AS DeletionTombstoneId,
              t.tenant_id AS TenantId,t.cleanup_job_id AS CleanupJobId,t.deletion_event_id AS DeletionEventId,
              t.data_type AS DataType,t.object_id AS ObjectId,t.deleted_at AS DeletedAt,
              d.store_type AS StoreType,d.attempts AS Attempts,a.snapshot_json::text AS SnapshotJson
            """, new { Worker = $"{Environment.MachineName}:{Environment.ProcessId}" }, cancellationToken: cancellationToken));
    }

    internal async Task ProcessAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        try
        {
            var proof = work.StoreType switch
            {
                "postgres" => await DeleteAuthorityAsync(work, cancellationToken),
                "pgvector" => await DeleteVectorAsync(work, cancellationToken),
                "arangodb" => await DeleteGraphAsync(work, cancellationToken),
                "redis" => await DeleteRedisAsync(work, cancellationToken),
                "object_storage" => await DeleteObjectsAsync(work, cancellationToken),
                "exports" => await DeleteExportsAsync(work, cancellationToken),
                "backup" => ScheduleBackupExpiry(work),
                _ => throw new StoreBlockedException("Unsupported store type")
            };
            await CompleteAsync(work, proof, cancellationToken);
        }
        catch (StoreBlockedException ex)
        {
            await BlockAsync(work, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            await FailAsync(work, ex, cancellationToken);
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO data_deletion_delivery(deletion_tombstone_id,store_type)
            SELECT t.deletion_tombstone_id,stores.store_type
            FROM data_deletion_tombstone t
            CROSS JOIN unnest(@Stores::varchar[]) AS stores(store_type)
            ON CONFLICT(deletion_tombstone_id,store_type) DO NOTHING
            """, new { Stores }, cancellationToken: cancellationToken));
    }

    private async Task<object> DeleteAuthorityAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        if (!AuthorityTables.TryGetValue(work.DataType, out var descriptor))
            throw new StoreBlockedException($"No authority-table mapping exists for {work.DataType}");
        if (!long.TryParse(work.ObjectId, out var objectId))
            throw new StoreBlockedException("Authority object id is not numeric");
        var tenant = descriptor.TenantColumn is null ? string.Empty : $" AND {descriptor.TenantColumn}=@TenantId";
        await using var connection = connectionFactory.CreateConnection();
        var remaining = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {descriptor.Table} WHERE {descriptor.IdColumn}=@ObjectId{tenant}",
            new { ObjectId = objectId, work.TenantId }, cancellationToken: cancellationToken));
        if (remaining != 0) throw new InvalidOperationException("Authority row still exists after retention deletion.");
        return new { verified = true, remaining, table = descriptor.Table, work.ObjectId };
    }

    private async Task<object> DeleteVectorAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM feature_embedding
            WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND
              ((@DataType='capture' AND capture_id::text=@ObjectId)
               OR metadata_json->>'capture_id'=@ObjectId
               OR metadata_json->>'event_id'=@ObjectId
               OR metadata_json->>'alert_id'=@ObjectId
               OR metadata_json->>'source_id'=@ObjectId)
            """, new { work.TenantId, work.DataType, work.ObjectId }, cancellationToken: cancellationToken));
        return new { deleted, matcher = work.DataType == "capture" ? "capture_id+metadata" : "metadata" };
    }

    private async Task<object> DeleteGraphAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        var health = await graph.GetHealthAsync(cancellationToken);
        if (!health.Available) throw new StoreBlockedException($"ArangoDB unavailable: {health.Error}");
        using var snapshot = JsonDocument.Parse(work.SnapshotJson);
        var matches = CollectIdentityValues(snapshot.RootElement, work.ObjectId);
        var deleted = 0L;
        foreach (var collection in GraphCollections)
        {
            var rows = await graph.QueryAsync("privacy-delete", """
                FOR document IN @@collection
                  FILTER (@tenantId == null OR document.tenant_id == @tenantId)
                  FILTER document._key IN @values OR TO_STRING(document.event_id) IN @values
                    OR TO_STRING(document.capture_id) IN @values OR TO_STRING(document.alert_id) IN @values
                    OR TO_STRING(document.source_id) IN @values OR TO_STRING(document.person_id) IN @values
                    OR TO_STRING(document.entity_id) IN @values OR TO_STRING(document.vid) IN @values
                  REMOVE document IN @@collection OPTIONS { ignoreErrors: true }
                  COLLECT WITH COUNT INTO removed RETURN removed
                """, new Dictionary<string, object?>
                {
                    ["@collection"] = collection,
                    ["tenantId"] = work.TenantId,
                    ["values"] = matches
                }, cancellationToken);
            if (rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 && rows[0].TryGetInt64(out var count))
                deleted += count;
        }
        return new { deleted, collections = GraphCollections.Length, identities = matches.Count, graph = health.Graph };
    }

    private async Task<object> DeleteRedisAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        if (!redis.Enabled) throw new StoreBlockedException("Redis is not configured or unavailable");
        var tenant = work.TenantId?.ToString() ?? "global";
        var patterns = new[]
        {
            $"*:{tenant}:*:{work.ObjectId}*", $"*:{tenant}:{work.ObjectId}*",
            $"vector:*:{work.ObjectId}*", $"capture:*:{work.ObjectId}*",
            $"alert:*:{work.ObjectId}*", $"event:*:{work.ObjectId}*"
        };
        var deleted = await redis.DeleteByPatternsAsync(patterns,
            configuration.GetValue("CommercialProduct:DataDeletion:RedisMaxKeys", 10_000), cancellationToken);
        return new { deleted, patterns, bounded = true };
    }

    private Task<object> DeleteObjectsAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = JsonDocument.Parse(work.SnapshotJson);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectStorageValues(snapshot.RootElement, null, candidates);
        var deleted = new List<string>();
        var absent = new List<string>();
        foreach (var candidate in candidates)
        {
            var path = ResolveStoragePath(candidate);
            if (path is null) continue;
            var relative = Path.GetRelativePath(storageRoot, path).Replace('\\', '/');
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted.Add(relative);
            }
            else absent.Add(relative);
        }
        return Task.FromResult<object>(new { deleted, alreadyAbsent = absent, candidates = candidates.Count });
    }

    private async Task<object> DeleteExportsAsync(DataDeletionDeliveryWork work, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ExportCandidate>(new CommandDefinition(
            """
            SELECT evidence_export_id AS EvidenceExportId,artifact_key AS ArtifactKey,
              manifest_json::text AS ManifestJson FROM evidence_export
            WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND status IN ('generating','ready')
            """, new { work.TenantId }, cancellationToken: cancellationToken))).AsList();
        var matched = rows.Where(row => ManifestReferences(row.ManifestJson, work.DataType, work.ObjectId)).ToArray();
        var deletedFiles = 0;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var export in matched)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE evidence_access_grant SET revoked_at=COALESCE(revoked_at,CURRENT_TIMESTAMP)
                WHERE evidence_export_id=@Id;
                UPDATE evidence_export SET status='revoked',completed_at=COALESCE(completed_at,CURRENT_TIMESTAMP)
                WHERE evidence_export_id=@Id
                """, new { Id = export.EvidenceExportId }, transaction, cancellationToken: cancellationToken));
            var path = ResolveExportPath(export.ArtifactKey);
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
                deletedFiles++;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new { revoked = matched.Length, deletedFiles };
    }

    private object ScheduleBackupExpiry(DataDeletionDeliveryWork work)
    {
        var days = Math.Clamp(configuration.GetValue("CommercialProduct:DataDeletion:BackupExpiryDays", 35), 1, 35);
        return new { scheduled = true, immutableBackup = true, expiresAt = work.DeletedAt.AddDays(days), maximumDays = days };
    }

    private async Task CompleteAsync(DataDeletionDeliveryWork work, object proof, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE data_deletion_delivery SET status='completed',proof_json=@Proof::jsonb,last_error=NULL,
              completed_at=CURRENT_TIMESTAMP,locked_by=NULL,lease_until=NULL,updated_at=CURRENT_TIMESTAMP
            WHERE delivery_id=@DeliveryId;
            UPDATE integration_outbox o SET status='processed',processed_at=CURRENT_TIMESTAMP,locked_by=NULL,
              lock_until=NULL,last_error=NULL
            WHERE o.outbox_id=@DeletionEventId AND NOT EXISTS(
              SELECT 1 FROM data_deletion_delivery d JOIN data_deletion_tombstone t
                ON t.deletion_tombstone_id=d.deletion_tombstone_id
              WHERE t.deletion_event_id=o.outbox_id AND d.status NOT IN ('completed','blocked'))
            """, new { work.DeliveryId, work.DeletionEventId, Proof = JsonSerializer.Serialize(proof) }, cancellationToken: cancellationToken));
    }

    private async Task BlockAsync(DataDeletionDeliveryWork work, string reason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Deletion delivery blocked. deliveryId={DeliveryId}, store={Store}, reason={Reason}", work.DeliveryId, work.StoreType, reason);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE data_deletion_delivery SET status='blocked',last_error=LEFT(@Reason,2000),
              proof_json=jsonb_build_object('blocked',true,'reason',@Reason),completed_at=CURRENT_TIMESTAMP,
              locked_by=NULL,lease_until=NULL,updated_at=CURRENT_TIMESTAMP WHERE delivery_id=@DeliveryId
            ;
            UPDATE integration_outbox o SET status='processed',processed_at=CURRENT_TIMESTAMP,locked_by=NULL,lock_until=NULL
            WHERE o.outbox_id=@DeletionEventId AND NOT EXISTS(
              SELECT 1 FROM data_deletion_delivery d JOIN data_deletion_tombstone t
                ON t.deletion_tombstone_id=d.deletion_tombstone_id
              WHERE t.deletion_event_id=o.outbox_id AND d.status NOT IN ('completed','blocked'))
            """, new { work.DeliveryId, work.DeletionEventId, Reason = reason }, cancellationToken: cancellationToken));
    }

    private async Task FailAsync(DataDeletionDeliveryWork work, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Deletion delivery failed. deliveryId={DeliveryId}, store={Store}", work.DeliveryId, work.StoreType);
        var maxAttempts = Math.Clamp(configuration.GetValue("CommercialProduct:DataDeletion:MaxAttempts", 8), 1, 30);
        var terminal = work.Attempts >= maxAttempts;
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE data_deletion_delivery SET status=CASE WHEN @Terminal THEN 'blocked' ELSE 'failed' END,
              available_at=CASE WHEN @Terminal THEN available_at ELSE CURRENT_TIMESTAMP+(LEAST(1800,POWER(2,LEAST(@Attempts,10))) * INTERVAL '1 second') END,
              last_error=LEFT(@Error,2000),proof_json=CASE WHEN @Terminal THEN jsonb_build_object('blocked',true,'reason',@Error) ELSE proof_json END,
              completed_at=CASE WHEN @Terminal THEN CURRENT_TIMESTAMP ELSE NULL END,locked_by=NULL,lease_until=NULL,updated_at=CURRENT_TIMESTAMP
            WHERE delivery_id=@DeliveryId
            ;
            UPDATE integration_outbox o SET status='processed',processed_at=CURRENT_TIMESTAMP,locked_by=NULL,lock_until=NULL
            WHERE @Terminal AND o.outbox_id=@DeletionEventId AND NOT EXISTS(
              SELECT 1 FROM data_deletion_delivery d JOIN data_deletion_tombstone t
                ON t.deletion_tombstone_id=d.deletion_tombstone_id
              WHERE t.deletion_event_id=o.outbox_id AND d.status NOT IN ('completed','blocked'))
            """, new { work.DeliveryId, work.DeletionEventId, work.Attempts, Terminal = terminal, Error = exception.Message }, cancellationToken: cancellationToken));
    }

    private string? ResolveStoragePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is not "file") return null;
        var normalized = value.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (normalized.StartsWith($"storage{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[("storage".Length + 1)..];
        var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(storageRoot, normalized));
        var prefix = storageRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private string? ResolveExportPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(storageRoot, value));
        var prefix = exportRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool ManifestReferences(string json, string dataType, string objectId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return false;
            return items.EnumerateArray().Any(item =>
                GetString(item, "sourceType").Equals(dataType, StringComparison.OrdinalIgnoreCase)
                && GetString(item, "sourceId").Equals(objectId, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }

    private static List<string> CollectIdentityValues(JsonElement root, string objectId)
    {
        var values = new HashSet<string>(StringComparer.Ordinal) { objectId };
        foreach (var name in new[] { "event_id", "capture_id", "alert_id", "source_id", "person_id", "entity_id", "vid" })
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (!string.IsNullOrWhiteSpace(text)) values.Add(text);
        }
        return values.ToList();
    }

    private static void CollectStorageValues(JsonElement element, string? propertyName, ISet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectStorageValues(property.Value, property.Name, values);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStorageValues(item, propertyName, values);
                break;
            case JsonValueKind.String when IsStorageProperty(propertyName):
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                break;
        }
    }

    private static bool IsStorageProperty(string? name) => name is not null &&
        (name.EndsWith("path", StringComparison.OrdinalIgnoreCase)
         || name.EndsWith("uri", StringComparison.OrdinalIgnoreCase)
         || name.EndsWith("url", StringComparison.OrdinalIgnoreCase)
         || name.Contains("object_key", StringComparison.OrdinalIgnoreCase));
    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private sealed record ExportCandidate(long EvidenceExportId, string ArtifactKey, string ManifestJson);
    private sealed class StoreBlockedException(string message) : Exception(message);
}

internal sealed record DataDeletionDeliveryWork(
    long DeliveryId,long DeletionTombstoneId,long? TenantId,long CleanupJobId,long? DeletionEventId,
    string DataType,string ObjectId,DateTimeOffset DeletedAt,string StoreType,int Attempts,string SnapshotJson);

internal sealed record DataDeletionDeliveryView(
    long DeliveryId,long DeletionTombstoneId,long? TenantId,long CleanupJobId,string DataType,string ObjectId,
    string StoreType,string Status,int Attempts,string? LastError,string ProofJson,DateTimeOffset? CompletedAt,
    DateTimeOffset AvailableAt,DateTimeOffset UpdatedAt);

internal sealed class DataDeletionProjectionWorker(
    DataDeletionProjectionService service,
    IConfiguration configuration,
    ILogger<DataDeletionProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CommercialProduct:DataDeletion:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var work = await service.ClaimAsync(stoppingToken);
                if (work is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                await service.ProcessAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Data deletion projection worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
