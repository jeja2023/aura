using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Vector;
using Dapper;
using Npgsql;

namespace Aura.Api.MediaAnalysis;

internal sealed record MediaAnalysisBusinessProjection(string? AuraTrackId, string? EntityId, long? CaptureId, long? EmbeddingId);

internal sealed class MediaAnalysisBusinessProjector(IConfiguration configuration)
{
    public async Task<MediaAnalysisBusinessProjection> ProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId,
        JsonElement payload,
        string? providerTrackId,
        string? confirmedEntityId,
        string? externalEmbeddingId,
        decimal? confidence,
        CancellationToken cancellationToken)
    {
        var camera = await GetCameraAsync(connection, transaction, inbox.SourceId, cancellationToken);
        var track = await UpsertTrackBindingAsync(
            connection, transaction, inbox, providerTrackId, confirmedEntityId, cancellationToken);

        await SaveDetectionFactAsync(connection, transaction, inbox, analysisEventId, payload, camera, providerTrackId, confidence, cancellationToken);
        var captureId = await SaveCaptureAsync(connection, transaction, inbox, analysisEventId, payload, camera, cancellationToken);
        var embeddingId = await SaveEmbeddingAsync(
            connection, transaction, inbox, analysisEventId, payload, track?.AuraTrackId, confirmedEntityId ?? track?.EntityId,
            externalEmbeddingId, captureId, cancellationToken);

        var identity = await ApplyIdentityCandidateAsync(
            connection, transaction, inbox, analysisEventId, payload, track, embeddingId, camera, confidence, cancellationToken);
        var effectiveEntityId = identity ?? confirmedEntityId ?? track?.EntityId;
        if (!string.IsNullOrWhiteSpace(effectiveEntityId))
        {
            await connection.ExecuteAsync(Command(
                "UPDATE media_analysis_event SET entity_id=@EntityId WHERE analysis_event_id=@AnalysisEventId",
                new { EntityId = Limit(effectiveEntityId, 128), AnalysisEventId = analysisEventId }, transaction, cancellationToken));
        }

        await SaveTrackEventAsync(
            connection, transaction, inbox, payload, camera, track?.AuraTrackId, effectiveEntityId, cancellationToken);
        await SaveBehaviorAlertAndJudgeAsync(
            connection, transaction, inbox, analysisEventId, payload, effectiveEntityId ?? track?.AuraTrackId, confidence, cancellationToken);
        await SaveArtifactAsync(connection, transaction, inbox.TenantId, analysisEventId, payload, cancellationToken);

        if (captureId.HasValue && embeddingId.HasValue)
        {
            await connection.ExecuteAsync(Command(
                "UPDATE capture_record SET embedding_id=@EmbeddingId WHERE capture_id=@CaptureId",
                new { EmbeddingId = embeddingId.Value, CaptureId = captureId.Value }, transaction, cancellationToken));
        }

        return new MediaAnalysisBusinessProjection(track?.AuraTrackId, effectiveEntityId, captureId, embeddingId);
    }

    private static async Task<CameraContext?> GetCameraAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long? sourceId,
        CancellationToken cancellationToken)
    {
        if (!sourceId.HasValue) return null;
        return await connection.QuerySingleOrDefaultAsync<CameraContext>(Command(
            """
            SELECT c.camera_id AS CameraId,c.device_id AS DeviceId,c.channel_no AS ChannelNo
            FROM media_source s JOIN map_camera c ON c.camera_id=s.camera_id
            WHERE s.source_id=@SourceId
            """, new { SourceId = sourceId.Value }, transaction, cancellationToken));
    }

    private static async Task<TrackBinding?> UpsertTrackBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        string? providerTrackId,
        string? entityId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerTrackId)) return null;
        var auraTrackId = StableTrackId(inbox.TenantId, inbox.ProviderId, inbox.SourceId, providerTrackId);
        var status = string.Equals(inbox.EventType, "track.ended", StringComparison.OrdinalIgnoreCase) ? "ended" : "active";
        return await connection.QuerySingleAsync<TrackBinding>(Command(
            """
            INSERT INTO media_track_binding(tenant_id,provider_id,source_id,provider_track_id,aura_track_id,
              entity_id,status,first_seen,last_seen,last_sequence_no)
            VALUES(@TenantId,@ProviderId,@SourceId,@ProviderTrackId,@AuraTrackId,@EntityId,@Status,@EventTime,@EventTime,@SequenceNo)
            ON CONFLICT(tenant_id,provider_id,source_id,provider_track_id) DO UPDATE SET
              entity_id=COALESCE(EXCLUDED.entity_id,media_track_binding.entity_id),
              status=CASE WHEN EXCLUDED.status='ended' THEN 'ended' ELSE media_track_binding.status END,
              first_seen=LEAST(media_track_binding.first_seen,EXCLUDED.first_seen),
              last_seen=GREATEST(media_track_binding.last_seen,EXCLUDED.last_seen),
              last_sequence_no=GREATEST(media_track_binding.last_sequence_no,EXCLUDED.last_sequence_no),updated_at=NOW()
            RETURNING track_binding_id AS TrackBindingId,aura_track_id AS AuraTrackId,entity_id AS EntityId
            """,
            new
            {
                inbox.TenantId,
                inbox.ProviderId,
                inbox.SourceId,
                ProviderTrackId = Limit(providerTrackId, 256),
                AuraTrackId = auraTrackId,
                EntityId = Limit(entityId, 128),
                Status = status,
                inbox.EventTime,
                inbox.SequenceNo
            }, transaction, cancellationToken));
    }

    private static async Task SaveDetectionFactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId,
        JsonElement payload,
        CameraContext? camera,
        string? providerTrackId,
        decimal? confidence,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(inbox.EventType, "object.detected", StringComparison.OrdinalIgnoreCase)) return;
        var objectType = Text(payload, "object_type") ?? Text(payload, "class") ?? "object";
        var bbox = payload.TryGetProperty("bbox", out var value) && value.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? value.GetRawText()
            : null;
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO media_detection_fact(analysis_event_id,tenant_id,source_id,camera_id,provider_track_id,
              object_type,bbox_json,confidence,model_code,model_version,event_time,detail_json)
            VALUES(@AnalysisEventId,@TenantId,@SourceId,@CameraId,@ProviderTrackId,@ObjectType,
              CASE WHEN @Bbox IS NULL THEN NULL ELSE CAST(@Bbox AS jsonb) END,@Confidence,@ModelCode,@ModelVersion,@EventTime,CAST(@Detail AS jsonb))
            ON CONFLICT(analysis_event_id) DO NOTHING
            """,
            new
            {
                AnalysisEventId = analysisEventId,
                inbox.TenantId,
                inbox.SourceId,
                CameraId = camera?.CameraId,
                ProviderTrackId = Limit(providerTrackId, 256),
                ObjectType = Limit(objectType, 128),
                Bbox = bbox,
                Confidence = ClampConfidence(confidence),
                ModelCode = Limit(Text(payload, "model_code"), 128),
                ModelVersion = Limit(Text(payload, "model_version"), 128),
                inbox.EventTime,
                Detail = payload.GetRawText()
            }, transaction, cancellationToken));
    }

    private static async Task<long?> SaveCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId,
        JsonElement payload,
        CameraContext? camera,
        CancellationToken cancellationToken)
    {
        if (camera is null) return null;
        var image = Text(payload, "snapshot_url") ?? Text(payload, "image_url");
        if (string.IsNullOrWhiteSpace(image)) return null;
        var inserted = await connection.ExecuteScalarAsync<long?>(Command(
            """
            INSERT INTO capture_record(device_id,channel_no,capture_time,image_path,metadata_json,tenant_id,camera_id,analysis_event_id,created_at)
            SELECT @DeviceId,@ChannelNo,@CaptureTime,@ImagePath,CAST(@Metadata AS jsonb),@TenantId,@CameraId,@AnalysisEventId,NOW()
            WHERE NOT EXISTS(SELECT 1 FROM capture_record WHERE analysis_event_id=@AnalysisEventId)
            RETURNING capture_id
            """,
            new
            {
                camera.DeviceId,
                camera.ChannelNo,
                CaptureTime = inbox.EventTime,
                ImagePath = image,
                Metadata = payload.GetRawText(),
                inbox.TenantId,
                camera.CameraId,
                AnalysisEventId = analysisEventId
            }, transaction, cancellationToken));
        return inserted ?? await connection.ExecuteScalarAsync<long?>(Command(
            "SELECT capture_id FROM capture_record WHERE analysis_event_id=@AnalysisEventId",
            new { AnalysisEventId = analysisEventId }, transaction, cancellationToken));
    }

    private static async Task<long?> SaveEmbeddingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId,
        JsonElement payload,
        string? auraTrackId,
        string? entityId,
        string? externalEmbeddingId,
        long? captureId,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("embedding", out var vectorElement) || vectorElement.ValueKind != JsonValueKind.Array) return null;
        float[] normalized;
        try
        {
            normalized = VectorValidation.Normalize(vectorElement.EnumerateArray().Select(value => value.GetSingle()).ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Embedding must contain 512 finite non-zero values.", ex);
        }

        var modelCode = Text(payload, "model_code") ?? "external-reid";
        var modelVersion = Text(payload, "model_version") ?? "default";
        var modelId = await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO embedding_model(provider_id,model_code,model_name,model_version,dimension,distance_metric)
            VALUES(@ProviderId,@ModelCode,@ModelCode,@ModelVersion,512,'cosine')
            ON CONFLICT(model_code,model_version) DO UPDATE SET updated_at=NOW()
            RETURNING model_id
            """, new { inbox.ProviderId, ModelCode = Limit(modelCode, 128), ModelVersion = Limit(modelVersion, 64) }, transaction, cancellationToken));
        var vid = Limit(entityId ?? auraTrackId ?? externalEmbeddingId ?? $"event-{inbox.InboxId}", 128)!;
        return await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO feature_embedding(tenant_id,external_embedding_id,vid,capture_id,model_id,feature,metadata_json)
            VALUES(@TenantId,@ExternalEmbeddingId,@Vid,@CaptureId,@ModelId,CAST(@Feature AS vector),CAST(@Metadata AS jsonb))
            ON CONFLICT(tenant_id,model_id,vid,capture_id) DO UPDATE SET
              external_embedding_id=EXCLUDED.external_embedding_id,feature=EXCLUDED.feature,
              metadata_json=EXCLUDED.metadata_json,updated_at=NOW()
            RETURNING embedding_id
            """,
            new
            {
                inbox.TenantId,
                ExternalEmbeddingId = Limit(externalEmbeddingId, 255),
                Vid = vid,
                CaptureId = captureId,
                ModelId = modelId,
                Feature = VectorValidation.ToSqlLiteral(normalized),
                Metadata = BuildEmbeddingMetadata(payload, inbox, analysisEventId)
            }, transaction, cancellationToken));
    }

    internal static string BuildEmbeddingMetadata(
        JsonElement payload,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in payload.EnumerateObject())
            {
                if (property.NameEquals("embedding") || property.NameEquals("_aura")) continue;
                property.WriteTo(writer);
            }
            writer.WritePropertyName("_aura");
            writer.WriteStartObject();
            writer.WriteNumber("analysis_event_id", analysisEventId);
            writer.WriteNumber("inbox_id", inbox.InboxId);
            writer.WriteNumber("provider_id", inbox.ProviderId);
            writer.WriteNumber("tenant_id", inbox.TenantId);
            writer.WriteString("event_id", inbox.EventId);
            writer.WriteString("event_type", inbox.EventType);
            if (inbox.SourceId.HasValue) writer.WriteNumber("source_id", inbox.SourceId.Value);
            if (!string.IsNullOrWhiteSpace(inbox.TraceId)) writer.WriteString("trace_id", inbox.TraceId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<string?> ApplyIdentityCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId,
        JsonElement payload,
        TrackBinding? track,
        long? embeddingId,
        CameraContext? camera,
        decimal? confidence,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(inbox.EventType, "identity.candidate", StringComparison.OrdinalIgnoreCase)) return null;
        var candidate = Limit(Text(payload, "entity_candidate_id") ?? Text(payload, "candidate_vid"), 64);
        var autoLink = configuration.GetValue("MediaAnalysis:Identity:AutoLinkThreshold", 0.92m);
        var review = configuration.GetValue("MediaAnalysis:Identity:ReviewThreshold", 0.75m);
        var score = ClampConfidence(confidence);
        var decision = string.IsNullOrWhiteSpace(candidate) || !score.HasValue
            ? "rejected"
            : score >= autoLink ? "linked" : score >= review ? "review" : "rejected";
        var reason = decision switch
        {
            "linked" => $"confidence >= {autoLink.ToString(CultureInfo.InvariantCulture)}",
            "review" => $"confidence between {review.ToString(CultureInfo.InvariantCulture)} and {autoLink.ToString(CultureInfo.InvariantCulture)}",
            _ => "candidate or confidence is missing/below policy threshold"
        };

        if (decision == "linked" && candidate is not null && camera is not null)
        {
            await connection.ExecuteAsync(Command(
                """
                INSERT INTO virtual_person(v_id,first_seen,last_seen,device_id,capture_count,created_at)
                VALUES(@Vid,@EventTime,@EventTime,@DeviceId,1,NOW())
                ON CONFLICT(v_id) DO UPDATE SET first_seen=LEAST(virtual_person.first_seen,EXCLUDED.first_seen),
                  last_seen=GREATEST(virtual_person.last_seen,EXCLUDED.last_seen),capture_count=virtual_person.capture_count+1
                """, new { Vid = candidate, inbox.EventTime, camera.DeviceId }, transaction, cancellationToken));
            if (track is not null)
            {
                await connection.ExecuteAsync(Command(
                    "UPDATE media_track_binding SET entity_id=@EntityId,updated_at=NOW() WHERE track_binding_id=@TrackBindingId",
                    new { EntityId = candidate, track.TrackBindingId }, transaction, cancellationToken));
                await connection.ExecuteAsync(Command(
                    "UPDATE track_event SET vid=@EntityId WHERE vid=@AuraTrackId",
                    new { EntityId = candidate, track.AuraTrackId }, transaction, cancellationToken));
                await connection.ExecuteAsync(Command(
                    "UPDATE feature_embedding SET vid=@EntityId,updated_at=NOW() WHERE tenant_id=@TenantId AND vid=@AuraTrackId",
                    new { EntityId = candidate, inbox.TenantId, track.AuraTrackId }, transaction, cancellationToken));
            }
        }

        await connection.ExecuteAsync(Command(
            """
            INSERT INTO media_identity_candidate(analysis_event_id,tenant_id,track_binding_id,candidate_vid,embedding_id,
              model_code,model_version,confidence,decision,decision_reason,decided_at)
            VALUES(@AnalysisEventId,@TenantId,@TrackBindingId,@CandidateVid,@EmbeddingId,@ModelCode,@ModelVersion,
              @Confidence,@Decision,@Reason,CASE WHEN @Decision='pending' THEN NULL ELSE NOW() END)
            ON CONFLICT(analysis_event_id) DO UPDATE SET embedding_id=COALESCE(EXCLUDED.embedding_id,media_identity_candidate.embedding_id),
              confidence=EXCLUDED.confidence,decision=EXCLUDED.decision,decision_reason=EXCLUDED.decision_reason,
              decided_at=EXCLUDED.decided_at
            """,
            new
            {
                AnalysisEventId = analysisEventId,
                inbox.TenantId,
                TrackBindingId = track?.TrackBindingId,
                CandidateVid = candidate,
                EmbeddingId = embeddingId,
                ModelCode = Limit(Text(payload, "model_code"), 128),
                ModelVersion = Limit(Text(payload, "model_version"), 128),
                Confidence = score,
                Decision = decision,
                Reason = reason
            }, transaction, cancellationToken));
        return decision == "linked" ? candidate : null;
    }

    private static async Task SaveTrackEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        JsonElement payload,
        CameraContext? camera,
        string? auraTrackId,
        string? entityId,
        CancellationToken cancellationToken)
    {
        if (!inbox.EventType.StartsWith("track.", StringComparison.OrdinalIgnoreCase)
            || camera is null || string.IsNullOrWhiteSpace(auraTrackId)) return;
        var roiId = await ResolveRoiAsync(connection, transaction, camera.CameraId, payload, cancellationToken);
        if (!roiId.HasValue) return;
        var vid = Limit(entityId ?? auraTrackId, 64)!;
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO track_event(vid,camera_id,roi_id,event_time)
            VALUES(@Vid,@CameraId,@RoiId,@EventTime)
            ON CONFLICT(vid,camera_id,event_time) DO NOTHING
            """, new { Vid = vid, camera.CameraId, RoiId = roiId.Value, inbox.EventTime }, transaction, cancellationToken));
    }

    private async Task SaveBehaviorAlertAndJudgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisInboxRecord inbox,
        long analysisEventId,
        JsonElement payload,
        string? vid,
        decimal? confidence,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(inbox.EventType, "behavior.detected", StringComparison.OrdinalIgnoreCase)) return;
        var behaviorType = Limit(Text(payload, "behavior_type") ?? Text(payload, "type") ?? "behavior", 128)!;
        var roomId = Number(payload, "room_id");
        var modelCode = Limit(Text(payload, "model_code"), 128);
        var modelVersion = Limit(Text(payload, "model_version"), 128);
        var score = ClampConfidence(confidence);
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO media_behavior_fact(analysis_event_id,tenant_id,behavior_type,provider_track_id,entity_id,
              room_id,model_code,model_version,confidence,event_time,detail_json)
            VALUES(@AnalysisEventId,@TenantId,@BehaviorType,@ProviderTrackId,@EntityId,@RoomId,@ModelCode,
              @ModelVersion,@Confidence,@EventTime,CAST(@Detail AS jsonb))
            ON CONFLICT(analysis_event_id) DO NOTHING
            """,
            new
            {
                AnalysisEventId = analysisEventId,
                inbox.TenantId,
                BehaviorType = behaviorType,
                ProviderTrackId = Limit(Text(payload, "track_id"), 256),
                EntityId = Limit(vid, 128),
                RoomId = roomId,
                ModelCode = modelCode,
                ModelVersion = modelVersion,
                Confidence = score,
                inbox.EventTime,
                Detail = payload.GetRawText()
            }, transaction, cancellationToken));

        var configured = configuration.GetSection("MediaAnalysis:BusinessRules:AlertBehaviorTypes").Get<string[]>()
            ?? ["intrusion", "loitering", "fall", "fight", "behavior"];
        var explicitAlert = Boolean(payload, "create_alert");
        if (explicitAlert || configured.Contains(behaviorType, StringComparer.OrdinalIgnoreCase))
        {
            await connection.ExecuteAsync(Command(
                """
                INSERT INTO alert_record(alert_type,vid,room_id,detail_json,created_at,tenant_id,analysis_event_id,model_code,model_version,confidence)
                VALUES(@AlertType,@Vid,@RoomId,CAST(@Detail AS jsonb),@EventTime,@TenantId,@AnalysisEventId,@ModelCode,@ModelVersion,@Confidence)
                ON CONFLICT(analysis_event_id) WHERE analysis_event_id IS NOT NULL DO NOTHING
                """,
                new
                {
                    AlertType = Limit(behaviorType, 32),
                    Vid = Limit(vid, 64),
                    RoomId = roomId,
                    Detail = payload.GetRawText(),
                    inbox.EventTime,
                    inbox.TenantId,
                    AnalysisEventId = analysisEventId,
                    ModelCode = modelCode,
                    ModelVersion = modelVersion,
                    Confidence = score
                }, transaction, cancellationToken));
        }

        var judgeType = Limit(Text(payload, "judge_type"), 32);
        if (!string.IsNullOrWhiteSpace(judgeType) && roomId.HasValue && !string.IsNullOrWhiteSpace(vid))
        {
            await connection.ExecuteAsync(Command(
                """
                INSERT INTO judge_result(vid,room_id,judge_type,judge_date,detail_json,created_at,tenant_id,
                  analysis_event_id,model_code,model_version,confidence)
                VALUES(@Vid,@RoomId,@JudgeType,@JudgeDate,CAST(@Detail AS jsonb),NOW(),@TenantId,
                  @AnalysisEventId,@ModelCode,@ModelVersion,@Confidence)
                ON CONFLICT(analysis_event_id) WHERE analysis_event_id IS NOT NULL DO NOTHING
                """,
                new
                {
                    Vid = Limit(vid, 64),
                    RoomId = roomId.Value,
                    JudgeType = judgeType,
                    JudgeDate = DateOnly.FromDateTime(inbox.EventTime).ToDateTime(TimeOnly.MinValue),
                    Detail = payload.GetRawText(),
                    inbox.TenantId,
                    AnalysisEventId = analysisEventId,
                    ModelCode = modelCode,
                    ModelVersion = modelVersion,
                    Confidence = score
                }, transaction, cancellationToken));
        }
    }

    private static async Task SaveArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long tenantId,
        long analysisEventId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var uri = Text(payload, "snapshot_url") ?? Text(payload, "artifact_url") ?? Text(payload, "uri");
        if (string.IsNullOrWhiteSpace(uri)) return;
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO media_artifact(tenant_id,analysis_event_id,provider_uri,media_type,content_type,size_bytes,sha256,archive_status,expires_at)
            VALUES(@TenantId,@AnalysisEventId,@Uri,@MediaType,@ContentType,@SizeBytes,@Sha256,'pending',@ExpiresAt)
            ON CONFLICT(analysis_event_id,provider_uri) DO NOTHING
            """,
            new
            {
                TenantId = tenantId,
                AnalysisEventId = analysisEventId,
                Uri = uri,
                MediaType = Limit(Text(payload, "media_type") ?? "snapshot", 64),
                ContentType = Limit(Text(payload, "content_type"), 128),
                SizeBytes = Number(payload, "size_bytes"),
                Sha256 = Limit(Text(payload, "sha256"), 64),
                ExpiresAt = Date(payload, "expires_at")
            }, transaction, cancellationToken));
    }

    private static async Task<long?> ResolveRoiAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long cameraId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var explicitRoi = Number(payload, "roi_id");
        if (explicitRoi.HasValue)
        {
            return await connection.ExecuteScalarAsync<long?>(Command(
                "SELECT roi_id FROM map_roi WHERE roi_id=@RoiId AND camera_id=@CameraId",
                new { RoiId = explicitRoi.Value, CameraId = cameraId }, transaction, cancellationToken));
        }

        var point = DetectionPoint(payload);
        if (!point.HasValue) return null;
        var candidates = (await connection.QueryAsync<RoiCandidate>(Command(
            "SELECT roi_id AS RoiId,vertices_json::text AS VerticesJson FROM map_roi WHERE camera_id=@CameraId ORDER BY roi_id",
            new { CameraId = cameraId }, transaction, cancellationToken))).AsList();
        return candidates.FirstOrDefault(candidate => RoiGeometry.Contains(candidate.VerticesJson, point.Value.X, point.Value.Y))?.RoiId;
    }

    private static (double X, double Y)? DetectionPoint(JsonElement payload)
    {
        if (payload.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Object
            && TryDouble(position, "x", out var positionX) && TryDouble(position, "y", out var positionY))
            return (positionX, positionY);
        if (TryDouble(payload, "pos_x", out var x) && TryDouble(payload, "pos_y", out var y)) return (x, y);
        if (payload.TryGetProperty("bbox", out var bbox) && bbox.ValueKind == JsonValueKind.Array)
        {
            var values = bbox.EnumerateArray().Take(4).Select(value => value.GetDouble()).ToArray();
            if (values.Length == 4) return (values[0] + values[2] / 2d, values[1] + values[3] / 2d);
        }
        return null;
    }

    internal static string StableTrackId(long tenantId, long providerId, long? sourceId, string providerTrackId)
    {
        var input = $"{tenantId}|{providerId}|{sourceId?.ToString(CultureInfo.InvariantCulture) ?? "none"}|{providerTrackId}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "T_" + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && (value.TryGetInt64(out var number)
            || (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))) ? number : null;
    private static DateTimeOffset? Date(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var date) ? date : null;
    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool TryDouble(JsonElement element, string property, out double result)
    {
        result = 0;
        return element.TryGetProperty(property, out var value) && value.TryGetDouble(out result);
    }
    private static decimal? ClampConfidence(decimal? value) => value.HasValue ? Math.Clamp(value.Value, 0m, 1m) : null;
    private static string? Limit(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static CommandDefinition Command(string sql, object? parameters, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private sealed record CameraContext(long CameraId, long DeviceId, int ChannelNo);
    private sealed record TrackBinding(long TrackBindingId, string AuraTrackId, string? EntityId);
    private sealed record RoiCandidate(long RoiId, string VerticesJson);
}

internal static class RoiGeometry
{
    internal static bool Contains(string verticesJson, double x, double y)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(verticesJson) ? "[]" : verticesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var points = new List<(double X, double Y)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("x", out var px) && px.TryGetDouble(out var objectX)
                    && item.TryGetProperty("y", out var py) && py.TryGetDouble(out var objectY))
                {
                    points.Add((objectX, objectY));
                }
                else if (item.ValueKind == JsonValueKind.Array)
                {
                    var coordinates = item.EnumerateArray().Take(2).ToArray();
                    if (coordinates.Length == 2
                        && coordinates[0].TryGetDouble(out var arrayX)
                        && coordinates[1].TryGetDouble(out var arrayY))
                    {
                        points.Add((arrayX, arrayY));
                    }
                }
            }
            if (points.Count < 3) return false;
            var inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var intersect = ((points[i].Y > y) != (points[j].Y > y))
                    && x < (points[j].X - points[i].X) * (y - points[i].Y)
                        / ((points[j].Y - points[i].Y) + double.Epsilon) + points[i].X;
                if (intersect) inside = !inside;
            }
            return inside;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
