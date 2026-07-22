using System.Data;
using System.Globalization;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;
using Npgsql;

namespace Aura.Api.MediaAnalysis;

internal sealed class InboxRepository(
    PgSqlConnectionFactory connectionFactory,
    IConfiguration configuration,
    MediaAnalysisBusinessProjector businessProjector)
{
    private static readonly HashSet<string> SupportedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "analysis.job.started", "analysis.job.progress", "analysis.job.completed", "analysis.job.failed",
        "stream.started", "stream.heartbeat", "stream.degraded", "stream.stopped",
        "object.detected", "track.started", "track.updated", "track.ended",
        "identity.candidate", "behavior.detected", "artifact.ready"
    };

    private const string InboxColumns = """
        inbox_id AS InboxId, provider_id AS ProviderId, tenant_id AS TenantId, event_id AS EventId,
        provider_event_id AS ProviderEventId, subscription_id AS SubscriptionId, source_id AS SourceId,
        sequence_no AS SequenceNo, schema_version AS SchemaVersion, event_type AS EventType,
        event_time AS EventTime, produced_at AS ProducedAt, received_at AS ReceivedAt,
        payload_json::text AS PayloadJson, payload_hash AS PayloadHash, status AS Status,
        attempt_count AS AttemptCount, trace_id AS TraceId
        """;

    public async Task<IReadOnlyList<MediaAnalysisInboxRecord>> ClaimAsync(
        string workerId,
        int limit,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<MediaAnalysisInboxRecord>(Command(
            $"""
            WITH due AS (
              SELECT i.inbox_id FROM media_analysis_inbox i
              LEFT JOIN media_analysis_subscription s ON s.subscription_id=i.subscription_id
              WHERE i.status IN ('received','retry_wait')
                AND (i.next_attempt_at IS NULL OR i.next_attempt_at<=NOW())
                AND (i.lock_until IS NULL OR i.lock_until<NOW())
                AND (i.subscription_id IS NULL OR i.sequence_no IS NULL
                  OR s.last_sequence_no IS NULL OR i.sequence_no<=s.last_sequence_no+1)
                AND NOT EXISTS (
                  SELECT 1 FROM media_analysis_inbox earlier
                  WHERE earlier.subscription_id=i.subscription_id
                    AND earlier.sequence_no IS NOT NULL AND earlier.sequence_no<i.sequence_no
                    AND earlier.status IN ('received','processing','retry_wait','dead_letter'))
              ORDER BY i.inbox_id FOR UPDATE OF i SKIP LOCKED LIMIT @Limit
            )
            UPDATE media_analysis_inbox i
            SET status='processing', attempt_count=attempt_count+1, locked_by=@WorkerId,
                lock_until=NOW()+@Lease
            FROM due WHERE i.inbox_id=due.inbox_id
            RETURNING {PrefixColumns(InboxColumns, "i")}
            """,
            new { WorkerId = workerId, Limit = Math.Clamp(limit, 1, 500), Lease = lease }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task ProcessAsync(MediaAnalysisInboxRecord inbox, CancellationToken cancellationToken)
    {
        if (!SupportedEventTypes.Contains(inbox.EventType))
        {
            await MarkUnsupportedAsync(inbox.InboxId, cancellationToken);
            return;
        }

        var envelope = JsonSerializer.Deserialize<MediaAnalysisEventEnvelope>(inbox.PayloadJson, MediaAnalysisJson.Options)
            ?? throw new InvalidDataException("Inbox event payload is not a valid standard event envelope.");
        var payload = envelope.Payload;
        var trackId = String(payload, "track_id");
        var entityId = String(payload, "entity_id");
        var embeddingId = String(payload, "embedding_id");
        var confidence = Decimal(payload, "confidence");
        var allowedLateness = TimeSpan.FromSeconds(configuration.GetValue("MediaAnalysis:Inbox:AllowedLatenessSeconds", 30));

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long? lastSequence = null;
        if (inbox.SubscriptionId.HasValue && inbox.SequenceNo.HasValue)
        {
            await connection.ExecuteAsync(Command(
                "SELECT pg_advisory_xact_lock(@SubscriptionId)",
                new { SubscriptionId = inbox.SubscriptionId.Value }, transaction, cancellationToken));
            lastSequence = await connection.ExecuteScalarAsync<long?>(Command(
                "SELECT last_sequence_no FROM media_analysis_subscription WHERE subscription_id=@SubscriptionId FOR UPDATE",
                new { SubscriptionId = inbox.SubscriptionId.Value }, transaction, cancellationToken));
            if (lastSequence.HasValue && inbox.SequenceNo.Value > lastSequence.Value + 1)
            {
                throw new InvalidOperationException(
                    $"Subscription sequence gap: expected {lastSequence.Value + 1}, received {inbox.SequenceNo.Value}.");
            }
        }

        var late = inbox.EventTime < inbox.ReceivedAt - allowedLateness
            || (lastSequence.HasValue && inbox.SequenceNo <= lastSequence);

        var analysisEventId = await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO media_analysis_event(inbox_id, tenant_id, source_id, subscription_id,
              event_type, event_time, track_id, entity_id, embedding_id, confidence, late_event, detail_json)
            VALUES(@InboxId,@TenantId,@SourceId,@SubscriptionId,@EventType,@EventTime,@TrackId,@EntityId,
              @EmbeddingId,@Confidence,@LateEvent,CAST(@DetailJson AS jsonb))
            ON CONFLICT(inbox_id) DO UPDATE SET inbox_id=EXCLUDED.inbox_id
            RETURNING analysis_event_id
            """,
            new
            {
                inbox.InboxId,
                inbox.TenantId,
                inbox.SourceId,
                inbox.SubscriptionId,
                inbox.EventType,
                inbox.EventTime,
                TrackId = trackId,
                EntityId = entityId,
                EmbeddingId = embeddingId,
                Confidence = confidence,
                LateEvent = late,
                DetailJson = payload.GetRawText()
            }, transaction, cancellationToken));

        await ApplyJobOrSubscriptionStateAsync(connection, transaction, inbox, payload, cancellationToken);
        var business = await businessProjector.ProjectAsync(
            connection, transaction, inbox, analysisEventId, payload, trackId, entityId, embeddingId, confidence, cancellationToken);

        var outboxPayload = JsonSerializer.Serialize(new
        {
            schema_version = "1.0",
            inbox_id = inbox.InboxId,
            analysis_event_id = analysisEventId,
            tenant_id = inbox.TenantId,
            source_id = inbox.SourceId,
            subscription_id = inbox.SubscriptionId,
            event_type = inbox.EventType,
            event_time = inbox.EventTime,
            track_id = business.AuraTrackId,
            provider_track_id = trackId,
            entity_id = business.EntityId,
            capture_id = business.CaptureId,
            embedding_id = business.EmbeddingId,
            confidence,
            late_event = late,
            detail = payload
        }, MediaAnalysisJson.Options);

        await connection.ExecuteAsync(Command(
            """
            INSERT INTO integration_outbox(tenant_id, aggregate_type, aggregate_id, event_type, payload_json)
            SELECT @TenantId,'media_analysis_event',@AggregateId,@EventType,CAST(@PayloadJson AS jsonb)
            WHERE NOT EXISTS (
              SELECT 1 FROM integration_outbox
              WHERE aggregate_type='media_analysis_event' AND aggregate_id=@AggregateId AND event_type=@EventType)
            """,
            new
            {
                inbox.TenantId,
                AggregateId = analysisEventId.ToString(CultureInfo.InvariantCulture),
                EventType = $"graph.{inbox.EventType}",
                PayloadJson = outboxPayload
            }, transaction, cancellationToken));

        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_inbox SET status='processed', processed_at=NOW(), locked_by=NULL,
              lock_until=NULL, next_attempt_at=NULL, last_error_code=NULL, last_error=NULL
            WHERE inbox_id=@InboxId
            """,
            new { inbox.InboxId }, transaction, cancellationToken));

        if (inbox.SubscriptionId.HasValue && inbox.SequenceNo.HasValue)
        {
            await connection.ExecuteAsync(Command(
                """
                UPDATE media_analysis_subscription SET
                  last_sequence_no=GREATEST(COALESCE(last_sequence_no,@SequenceNo),@SequenceNo),
                  last_event_time=GREATEST(COALESCE(last_event_time,@EventTime),@EventTime),
                  updated_at=NOW()
                WHERE subscription_id=@SubscriptionId
                """,
                new
                {
                    SubscriptionId = inbox.SubscriptionId.Value,
                    SequenceNo = inbox.SequenceNo.Value,
                    inbox.EventTime
                }, transaction, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailureAsync(long inboxId, int attempts, Exception exception, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(configuration.GetValue("MediaAnalysis:Inbox:MaxAttempts", 8), 1, 100);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_inbox SET
              status=CASE WHEN @Attempts>=@MaxAttempts THEN 'dead_letter' ELSE 'retry_wait' END,
              next_attempt_at=CASE WHEN @Attempts>=@MaxAttempts THEN NULL
                ELSE NOW()+(LEAST(900, POWER(2, LEAST(@Attempts,10))) * INTERVAL '1 second') END,
              locked_by=NULL, lock_until=NULL, last_error_code=@ErrorCode, last_error=LEFT(@Error,2000)
            WHERE inbox_id=@InboxId
            """,
            new
            {
                InboxId = inboxId,
                Attempts = attempts,
                MaxAttempts = maxAttempts,
                ErrorCode = exception.GetType().Name,
                Error = exception.Message
            }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MediaAnalysisInboxRecord>> QueryAsync(long? tenantId, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<MediaAnalysisInboxRecord>(Command(
            $"""
            SELECT {InboxColumns} FROM media_analysis_inbox
            WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND (@Status IS NULL OR status=@Status)
            ORDER BY inbox_id DESC LIMIT @Limit
            """,
            new { TenantId = tenantId, Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(), Limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<int> ReplayAsync(ReplayRequest request, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (request.InboxIds is { Count: > 0 })
        {
            return await connection.ExecuteAsync(Command(
                """
                UPDATE media_analysis_inbox SET status='received', next_attempt_at=NOW(), locked_by=NULL,
                  lock_until=NULL, processed_at=NULL, last_error_code=NULL, last_error=NULL
                WHERE inbox_id=ANY(@Ids) AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  AND status IN ('dead_letter','unsupported','retry_wait')
                """,
                new { Ids = request.InboxIds.Distinct().Take(1000).ToArray(), request.TenantId }, cancellationToken: cancellationToken));
        }

        if (request.EventIds is { Count: > 0 })
        {
            return await connection.ExecuteAsync(Command(
                """
                UPDATE media_analysis_inbox SET status='received',next_attempt_at=NOW(),locked_by=NULL,
                  lock_until=NULL,processed_at=NULL,last_error_code=NULL,last_error=NULL
                WHERE event_id=ANY(@EventIds) AND (@TenantId IS NULL OR tenant_id=@TenantId)
                  AND status IN ('dead_letter','unsupported','retry_wait','processed')
                """,
                new { EventIds = request.EventIds.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct().Take(1000).ToArray(), request.TenantId }, cancellationToken: cancellationToken));
        }

        return await connection.ExecuteAsync(Command(
            """
            WITH selected AS (
              SELECT inbox_id FROM media_analysis_inbox
              WHERE status=@Status AND (@TenantId IS NULL OR tenant_id=@TenantId)
                AND (@From IS NULL OR received_at>=@From) AND (@To IS NULL OR received_at<=@To)
              ORDER BY inbox_id LIMIT @Limit FOR UPDATE SKIP LOCKED)
            UPDATE media_analysis_inbox i SET status='received', next_attempt_at=NOW(), locked_by=NULL,
              lock_until=NULL, processed_at=NULL, last_error_code=NULL, last_error=NULL
            FROM selected WHERE i.inbox_id=selected.inbox_id
            """,
            new
            {
                Status = string.IsNullOrWhiteSpace(request.Status) ? "dead_letter" : request.Status.Trim(),
                request.From,
                request.To,
                request.TenantId,
                Limit = Math.Clamp(request.Limit, 1, 1000)
            }, cancellationToken: cancellationToken));
    }

    public async Task<object> GetStatsAsync(long? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<InboxStatusCount>(Command(
            "SELECT status AS Status, COUNT(*) AS Count, MIN(received_at) AS OldestAt FROM media_analysis_inbox WHERE (@TenantId IS NULL OR tenant_id=@TenantId) GROUP BY status ORDER BY status",
            new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
        return new { statuses = rows, total = rows.Sum(x => x.Count) };
    }

    private static async Task MarkUnsupportedAsync(long inboxId, CancellationToken cancellationToken, PgSqlConnectionFactory? factory = null)
    {
        if (factory is null)
        {
            throw new InvalidOperationException("Repository factory was not supplied.");
        }
        await using var connection = factory.CreateConnection();
        await connection.ExecuteAsync(Command(
            "UPDATE media_analysis_inbox SET status='unsupported', processed_at=NOW(), locked_by=NULL, lock_until=NULL WHERE inbox_id=@InboxId",
            new { InboxId = inboxId }, cancellationToken: cancellationToken));
    }

    private Task MarkUnsupportedAsync(long inboxId, CancellationToken cancellationToken) =>
        MarkUnsupportedAsync(inboxId, cancellationToken, connectionFactory);

    private static async Task ApplyJobOrSubscriptionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (inbox.EventType.StartsWith("stream.", StringComparison.OrdinalIgnoreCase) && inbox.SubscriptionId.HasValue)
        {
            var state = inbox.EventType.ToLowerInvariant() switch
            {
                "stream.started" or "stream.heartbeat" => "running",
                "stream.degraded" => "degraded",
                "stream.stopped" => "stopped",
                _ => "unknown"
            };
            await connection.ExecuteAsync(Command(
                """
                UPDATE media_analysis_subscription SET observed_state=@State,
                  last_heartbeat_at=CASE WHEN @State IN ('running','degraded') THEN @EventTime ELSE last_heartbeat_at END,
                  last_error=CASE WHEN @State='degraded' THEN LEFT(@Error,2000) ELSE NULL END,
                  updated_at=NOW() WHERE subscription_id=@SubscriptionId
                """,
                new { SubscriptionId = inbox.SubscriptionId.Value, State = state, inbox.EventTime, Error = String(payload, "message") }, transaction, cancellationToken));
        }

        if (inbox.EventType.StartsWith("analysis.job.", StringComparison.OrdinalIgnoreCase))
        {
            var externalId = String(payload, "job_id") ?? String(payload, "external_job_id");
            if (!string.IsNullOrWhiteSpace(externalId))
            {
                var state = inbox.EventType.ToLowerInvariant() switch
                {
                    "analysis.job.started" => "running",
                    "analysis.job.progress" => "running",
                    "analysis.job.completed" => "completed",
                    "analysis.job.failed" => "failed",
                    _ => "accepted"
                };
                await connection.ExecuteAsync(Command(
                    """
                    UPDATE media_analysis_job SET status=@State, progress=COALESCE(@Progress,progress),
                      result_json=CASE WHEN @State='completed' THEN CAST(@ResultJson AS jsonb) ELSE result_json END,
                      started_at=CASE WHEN @State='running' THEN COALESCE(started_at,@EventTime) ELSE started_at END,
                      completed_at=CASE WHEN @State IN ('completed','failed') THEN @EventTime ELSE completed_at END,
                      error_code=CASE WHEN @State='failed' THEN @ErrorCode ELSE NULL END,
                      error_message=CASE WHEN @State='failed' THEN LEFT(@ErrorMessage,2000) ELSE NULL END,
                      updated_at=NOW() WHERE provider_id=@ProviderId AND external_job_id=@ExternalId
                    """,
                    new
                    {
                        State = state,
                        Progress = Decimal(payload, "progress"),
                        ResultJson = payload.GetRawText(),
                        inbox.EventTime,
                        ErrorCode = String(payload, "error_code"),
                        ErrorMessage = String(payload, "error_message"),
                        inbox.ProviderId,
                        ExternalId = externalId
                    }, transaction, cancellationToken));
            }
        }
    }

    private static async Task ApplyTrackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        JsonElement payload,
        string? trackId,
        string? entityId,
        CancellationToken cancellationToken)
    {
        if (!inbox.EventType.StartsWith("track.", StringComparison.OrdinalIgnoreCase) || !inbox.SourceId.HasValue || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        var cameraId = await connection.ExecuteScalarAsync<long?>(Command(
            "SELECT camera_id FROM media_source WHERE source_id=@SourceId",
            new { SourceId = inbox.SourceId.Value }, transaction, cancellationToken));
        if (!cameraId.HasValue) return;

        var roiId = Long(payload, "roi_id") ?? await connection.ExecuteScalarAsync<long?>(Command(
            "SELECT roi_id FROM map_roi WHERE camera_id=@CameraId ORDER BY roi_id LIMIT 1",
            new { CameraId = cameraId.Value }, transaction, cancellationToken));
        if (!roiId.HasValue) return;

        var vid = (entityId ?? trackId).Length > 64 ? (entityId ?? trackId)[..64] : entityId ?? trackId;
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO track_event(vid,camera_id,roi_id,event_time)
            SELECT @Vid,@CameraId,@RoiId,@EventTime
            WHERE NOT EXISTS (SELECT 1 FROM track_event WHERE vid=@Vid AND camera_id=@CameraId AND event_time=@EventTime)
            """,
            new { Vid = vid, CameraId = cameraId.Value, RoiId = roiId.Value, inbox.EventTime }, transaction, cancellationToken));
    }

    private static async Task ApplyArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long tenantId,
        long analysisEventId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var uri = String(payload, "snapshot_url") ?? String(payload, "artifact_url") ?? String(payload, "uri");
        if (string.IsNullOrWhiteSpace(uri)) return;
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO media_artifact(tenant_id,analysis_event_id,provider_uri,media_type,content_type,size_bytes,sha256,archive_status,expires_at)
            SELECT @TenantId,@AnalysisEventId,@Uri,@MediaType,@ContentType,@SizeBytes,@Sha256,'pending',@ExpiresAt
            WHERE NOT EXISTS (SELECT 1 FROM media_artifact WHERE analysis_event_id=@AnalysisEventId AND provider_uri=@Uri)
            """,
            new
            {
                TenantId = tenantId,
                AnalysisEventId = analysisEventId,
                Uri = uri,
                MediaType = String(payload, "media_type") ?? "snapshot",
                ContentType = String(payload, "content_type"),
                SizeBytes = Long(payload, "size_bytes"),
                Sha256 = String(payload, "sha256"),
                ExpiresAt = Date(payload, "expires_at")
            }, transaction, cancellationToken));
    }

    private static async Task ApplyEmbeddingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        JsonElement payload,
        string? trackId,
        string? entityId,
        string? externalEmbeddingId,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("embedding", out var vectorElement) || vectorElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var values = vectorElement.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (values.Length != 512 || values.Any(x => !float.IsFinite(x)))
        {
            throw new InvalidDataException("Embedding must contain 512 finite values.");
        }
        float[] normalized;
        try
        {
            normalized = Aura.Api.Vector.VectorValidation.Normalize(values);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        var modelCode = String(payload, "model_code") ?? "external-reid";
        var modelVersion = String(payload, "model_version") ?? "default";
        var modelId = await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO embedding_model(provider_id,model_code,model_name,model_version,dimension,distance_metric)
            VALUES(@ProviderId,@ModelCode,@ModelCode,@ModelVersion,512,'cosine')
            ON CONFLICT(model_code,model_version) DO UPDATE SET updated_at=NOW()
            RETURNING model_id
            """,
            new { inbox.ProviderId, ModelCode = modelCode, ModelVersion = modelVersion }, transaction, cancellationToken));
        var vid = entityId ?? trackId ?? externalEmbeddingId ?? $"inbox-{inbox.InboxId}";
        var vector = Aura.Api.Vector.VectorValidation.ToSqlLiteral(normalized);
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO feature_embedding(tenant_id,external_embedding_id,vid,model_id,feature,metadata_json)
            VALUES(@TenantId,@ExternalEmbeddingId,@Vid,@ModelId,CAST(@Vector AS vector),CAST(@Metadata AS jsonb))
            ON CONFLICT(tenant_id,model_id,vid,capture_id) DO UPDATE SET
              external_embedding_id=EXCLUDED.external_embedding_id, feature=EXCLUDED.feature,
              metadata_json=EXCLUDED.metadata_json, updated_at=NOW()
            """,
            new
            {
                inbox.TenantId,
                ExternalEmbeddingId = externalEmbeddingId,
                Vid = vid.Length > 128 ? vid[..128] : vid,
                ModelId = modelId,
                Vector = vector,
                Metadata = payload.GetRawText()
            }, transaction, cancellationToken));
    }

    private static async Task ApplyAlertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        JsonElement payload,
        string? vid,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(inbox.EventType, "behavior.detected", StringComparison.OrdinalIgnoreCase)) return;
        var alertType = String(payload, "behavior_type") ?? String(payload, "type") ?? "behavior";
        var roomId = Long(payload, "room_id");
        await connection.ExecuteAsync(Command(
            "INSERT INTO alert_record(alert_type,vid,room_id,detail_json,created_at) VALUES(@AlertType,@Vid,@RoomId,CAST(@Detail AS jsonb),@EventTime)",
            new
            {
                AlertType = alertType.Length > 32 ? alertType[..32] : alertType,
                Vid = vid is { Length: > 64 } ? vid[..64] : vid,
                RoomId = roomId,
                Detail = payload.GetRawText(),
                inbox.EventTime
            }, transaction, cancellationToken));
    }

    private static string? String(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static decimal? Decimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDecimal(out var result) ? result : null;

    private static long? Long(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.TryGetInt64(out var result)) return result;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out result) ? result : null;
    }

    private static DateTimeOffset? Date(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;

    private static string PrefixColumns(string columns, string alias) =>
        string.Join(", ", columns.Split(',').Select(column => $"{alias}.{column.Trim()}"));

    private static CommandDefinition Command(string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private sealed record InboxStatusCount(string Status, long Count, DateTime? OldestAt);
}
