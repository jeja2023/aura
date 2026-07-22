using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Aura.Api.Data;

namespace Aura.Api.MediaAnalysis;

internal sealed class MediaAnalysisRepository(PgSqlConnectionFactory connectionFactory)
{
    private const string ProviderColumns = """
        provider_id AS ProviderId, tenant_id AS TenantId, provider_code AS ProviderCode,
        display_name AS DisplayName, adapter_type AS AdapterType, base_url AS BaseUrl,
        auth_type AS AuthType, secret_ref AS SecretRef, webhook_auth_type AS WebhookAuthType,
        webhook_secret_ref AS WebhookSecretRef, capabilities_json::text AS CapabilitiesJson,
        protocol_version AS ProtocolVersion, timeout_seconds AS TimeoutSeconds,
        max_concurrency AS MaxConcurrency, enabled AS Enabled,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string PipelineColumns = """
        pipeline_id AS PipelineId, provider_id AS ProviderId, pipeline_code AS PipelineCode,
        display_name AS DisplayName, model_name AS ModelName, model_version AS ModelVersion,
        input_types_json::text AS InputTypesJson, output_types_json::text AS OutputTypesJson,
        embedding_dimension AS EmbeddingDimension, default_options_json::text AS DefaultOptionsJson,
        enabled AS Enabled, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string SourceColumns = """
        source_id AS SourceId, tenant_id AS TenantId, camera_id AS CameraId, source_code AS SourceCode,
        source_type AS SourceType, uri_template AS UriTemplate, credential_ref AS CredentialRef,
        stream_profile AS StreamProfile, config_json::text AS ConfigJson, config_version AS ConfigVersion,
        enabled AS Enabled, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string SubscriptionColumns = """
        subscription_id AS SubscriptionId, tenant_id AS TenantId, provider_id AS ProviderId,
        source_id AS SourceId, pipeline_id AS PipelineId, client_subscription_id AS ClientSubscriptionId,
        external_subscription_id AS ExternalSubscriptionId, desired_state AS DesiredState,
        observed_state AS ObservedState, desired_config_json::text AS DesiredConfigJson,
        applied_config_hash AS AppliedConfigHash, last_heartbeat_at AS LastHeartbeatAt,
        last_reconciled_at AS LastReconciledAt, retry_count AS RetryCount, next_retry_at AS NextRetryAt,
        last_error AS LastError, version AS Version, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private const string JobColumns = """
        job_id AS JobId, tenant_id AS TenantId, provider_id AS ProviderId, pipeline_id AS PipelineId,
        source_id AS SourceId, idempotency_key AS IdempotencyKey, external_job_id AS ExternalJobId,
        media_type AS MediaType, media_uri AS MediaUri, request_json::text AS RequestJson,
        result_json::text AS ResultJson, status AS Status, progress AS Progress, retry_count AS RetryCount,
        next_retry_at AS NextRetryAt, error_code AS ErrorCode, error_message AS ErrorMessage,
        submitted_at AS SubmittedAt, started_at AS StartedAt, completed_at AS CompletedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public async Task<IReadOnlyList<MediaAnalysisProviderRecord>> ListProvidersAsync(long? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<MediaAnalysisProviderRecord>(Command(
            $"SELECT {ProviderColumns} FROM media_analysis_provider WHERE (@TenantId IS NULL OR tenant_id IS NULL OR tenant_id=@TenantId) ORDER BY provider_id",
            new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<MediaAnalysisProviderRecord?> GetProviderAsync(long providerId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisProviderRecord>(Command(
            $"SELECT {ProviderColumns} FROM media_analysis_provider WHERE provider_id=@ProviderId",
            new { ProviderId = providerId }, cancellationToken: cancellationToken));
    }

    public async Task<MediaAnalysisProviderRecord?> GetEnabledProviderByCodeAsync(string providerCode, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisProviderRecord>(Command(
            $"SELECT {ProviderColumns} FROM media_analysis_provider WHERE provider_code=@ProviderCode AND enabled=TRUE ORDER BY tenant_id NULLS FIRST LIMIT 1",
            new { ProviderCode = providerCode }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MediaAnalysisProviderRecord>> ListEnabledProvidersByCodeAsync(
        string providerCode,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<MediaAnalysisProviderRecord>(Command(
            $"SELECT {ProviderColumns} FROM media_analysis_provider WHERE provider_code=@ProviderCode AND enabled=TRUE ORDER BY tenant_id NULLS FIRST,provider_id",
            new { ProviderCode = providerCode }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<long> UpsertProviderAsync(long? providerId, ProviderUpsertRequest request, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (providerId.HasValue)
        {
            return await connection.ExecuteScalarAsync<long>(Command(
                """
                UPDATE media_analysis_provider SET
                  tenant_id=@TenantId, provider_code=@ProviderCode, display_name=@DisplayName,
                  adapter_type=@AdapterType, base_url=@BaseUrl, auth_type=@AuthType, secret_ref=@SecretRef,
                  webhook_auth_type=@WebhookAuthType, webhook_secret_ref=@WebhookSecretRef,
                  protocol_version=@ProtocolVersion, timeout_seconds=@TimeoutSeconds,
                  max_concurrency=@MaxConcurrency, enabled=@Enabled, updated_at=NOW()
                WHERE provider_id=@ProviderId
                RETURNING provider_id
                """,
                new
                {
                    ProviderId = providerId.Value,
                    request.TenantId,
                    ProviderCode = request.ProviderCode.Trim(),
                    DisplayName = request.DisplayName.Trim(),
                    AdapterType = Default(request.AdapterType, "standard_http"),
                    BaseUrl = request.BaseUrl.TrimEnd('/'),
                    AuthType = Default(request.AuthType, "hmac"),
                    request.SecretRef,
                    WebhookAuthType = Default(request.WebhookAuthType, "hmac"),
                    request.WebhookSecretRef,
                    ProtocolVersion = Default(request.ProtocolVersion, "1.0"),
                    TimeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? 10, 1, 300),
                    MaxConcurrency = Math.Clamp(request.MaxConcurrency ?? 16, 1, 10000),
                    request.Enabled
                }, cancellationToken: cancellationToken));
        }

        return await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO media_analysis_provider(
              tenant_id, provider_code, display_name, adapter_type, base_url, auth_type, secret_ref,
              webhook_auth_type, webhook_secret_ref,
              protocol_version, timeout_seconds, max_concurrency, enabled)
            VALUES(@TenantId, @ProviderCode, @DisplayName, @AdapterType, @BaseUrl, @AuthType, @SecretRef,
                   @WebhookAuthType, @WebhookSecretRef,
                   @ProtocolVersion, @TimeoutSeconds, @MaxConcurrency, @Enabled)
            RETURNING provider_id
            """,
            new
            {
                request.TenantId,
                ProviderCode = request.ProviderCode.Trim(),
                DisplayName = request.DisplayName.Trim(),
                AdapterType = Default(request.AdapterType, "standard_http"),
                BaseUrl = request.BaseUrl.TrimEnd('/'),
                AuthType = Default(request.AuthType, "hmac"),
                request.SecretRef,
                WebhookAuthType = Default(request.WebhookAuthType, "hmac"),
                request.WebhookSecretRef,
                ProtocolVersion = Default(request.ProtocolVersion, "1.0"),
                TimeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? 10, 1, 300),
                MaxConcurrency = Math.Clamp(request.MaxConcurrency ?? 16, 1, 10000),
                request.Enabled
            }, cancellationToken: cancellationToken));
    }

    public async Task UpdateCapabilitiesAsync(long providerId, ProviderCapabilities capabilities, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(capabilities, MediaAnalysisJson.Options);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            "UPDATE media_analysis_provider SET capabilities_json=CAST(@Json AS jsonb), protocol_version=@Version, updated_at=NOW() WHERE provider_id=@ProviderId",
            new { ProviderId = providerId, Json = json, Version = capabilities.ProtocolVersion }, cancellationToken: cancellationToken));
    }

    public async Task<bool> IsValidBindingAsync(
        long tenantId,
        long providerId,
        long pipelineId,
        long? sourceId,
        string inputType,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<bool>(Command(
            """
            SELECT
              EXISTS(SELECT 1 FROM media_analysis_provider
                WHERE provider_id=@ProviderId AND enabled=TRUE AND (tenant_id IS NULL OR tenant_id=@TenantId))
              AND EXISTS(SELECT 1 FROM media_analysis_pipeline
                WHERE pipeline_id=@PipelineId AND provider_id=@ProviderId AND enabled=TRUE
                  AND input_types_json ? @InputType)
              AND (@SourceId IS NULL OR EXISTS(SELECT 1 FROM media_source
                WHERE source_id=@SourceId AND tenant_id=@TenantId AND enabled=TRUE))
            """,
            new { TenantId = tenantId, ProviderId = providerId, PipelineId = pipelineId, SourceId = sourceId, InputType = inputType },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MediaAnalysisPipelineRecord>> ListPipelinesAsync(long? providerId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var sql = providerId.HasValue
            ? $"SELECT {PipelineColumns} FROM media_analysis_pipeline WHERE provider_id=@ProviderId ORDER BY pipeline_id"
            : $"SELECT {PipelineColumns} FROM media_analysis_pipeline ORDER BY pipeline_id";
        return (await connection.QueryAsync<MediaAnalysisPipelineRecord>(Command(sql, new { ProviderId = providerId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<long> UpsertPipelineAsync(long? pipelineId, PipelineCreateRequest request, CancellationToken cancellationToken)
    {
        var inputJson = JsonSerializer.Serialize(request.InputTypes ?? ["image"], MediaAnalysisJson.Options);
        var outputJson = JsonSerializer.Serialize(request.OutputTypes ?? [], MediaAnalysisJson.Options);
        var optionsJson = Json(request.DefaultOptions);
        await using var connection = connectionFactory.CreateConnection();
        if (pipelineId.HasValue)
        {
            return await connection.ExecuteScalarAsync<long>(Command(
                """
                UPDATE media_analysis_pipeline SET provider_id=@ProviderId, pipeline_code=@PipelineCode,
                  display_name=@DisplayName, model_name=@ModelName, model_version=@ModelVersion,
                  input_types_json=CAST(@InputJson AS jsonb), output_types_json=CAST(@OutputJson AS jsonb),
                  embedding_dimension=@EmbeddingDimension, default_options_json=CAST(@OptionsJson AS jsonb),
                  enabled=@Enabled, updated_at=NOW()
                WHERE pipeline_id=@PipelineId RETURNING pipeline_id
                """,
                new
                {
                    PipelineId = pipelineId.Value,
                    request.ProviderId,
                    PipelineCode = request.PipelineCode.Trim(),
                    DisplayName = request.DisplayName.Trim(),
                    ModelName = request.ModelName?.Trim() ?? string.Empty,
                    ModelVersion = Default(request.ModelVersion, "default"),
                    InputJson = inputJson,
                    OutputJson = outputJson,
                    request.EmbeddingDimension,
                    OptionsJson = optionsJson,
                    request.Enabled
                }, cancellationToken: cancellationToken));
        }

        return await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO media_analysis_pipeline(provider_id, pipeline_code, display_name, model_name, model_version,
              input_types_json, output_types_json, embedding_dimension, default_options_json, enabled)
            VALUES(@ProviderId, @PipelineCode, @DisplayName, @ModelName, @ModelVersion,
              CAST(@InputJson AS jsonb), CAST(@OutputJson AS jsonb), @EmbeddingDimension, CAST(@OptionsJson AS jsonb), @Enabled)
            RETURNING pipeline_id
            """,
            new
            {
                request.ProviderId,
                PipelineCode = request.PipelineCode.Trim(),
                DisplayName = request.DisplayName.Trim(),
                ModelName = request.ModelName?.Trim() ?? string.Empty,
                ModelVersion = Default(request.ModelVersion, "default"),
                InputJson = inputJson,
                OutputJson = outputJson,
                request.EmbeddingDimension,
                OptionsJson = optionsJson,
                request.Enabled
            }, cancellationToken: cancellationToken));
    }

    public async Task<MediaAnalysisPipelineRecord?> GetPipelineAsync(long pipelineId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisPipelineRecord>(Command(
            $"SELECT {PipelineColumns} FROM media_analysis_pipeline WHERE pipeline_id=@PipelineId",
            new { PipelineId = pipelineId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MediaSourceRecord>> ListSourcesAsync(long? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var sql = tenantId.HasValue
            ? $"SELECT {SourceColumns} FROM media_source WHERE tenant_id=@TenantId ORDER BY source_id"
            : $"SELECT {SourceColumns} FROM media_source ORDER BY source_id";
        return (await connection.QueryAsync<MediaSourceRecord>(Command(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<long> UpsertSourceAsync(long? sourceId, MediaSourceCreateRequest request, CancellationToken cancellationToken)
    {
        var configJson = Json(request.Config);
        await using var connection = connectionFactory.CreateConnection();
        if (sourceId.HasValue)
        {
            return await connection.ExecuteScalarAsync<long>(Command(
                """
                UPDATE media_source SET tenant_id=@TenantId, camera_id=@CameraId, source_code=@SourceCode,
                  source_type=@SourceType, uri_template=@UriTemplate, credential_ref=@CredentialRef,
                  stream_profile=@StreamProfile, config_json=CAST(@ConfigJson AS jsonb),
                  config_version=config_version+1, enabled=@Enabled, updated_at=NOW()
                WHERE source_id=@SourceId RETURNING source_id
                """,
                new
                {
                    SourceId = sourceId.Value,
                    request.TenantId,
                    request.CameraId,
                    SourceCode = request.SourceCode.Trim(),
                    SourceType = Default(request.SourceType, "rtsp"),
                    UriTemplate = request.UriTemplate.Trim(),
                    request.CredentialRef,
                    StreamProfile = Default(request.StreamProfile, "sub"),
                    ConfigJson = configJson,
                    request.Enabled
                }, cancellationToken: cancellationToken));
        }

        return await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO media_source(tenant_id, camera_id, source_code, source_type, uri_template,
              credential_ref, stream_profile, config_json, enabled)
            VALUES(@TenantId, @CameraId, @SourceCode, @SourceType, @UriTemplate,
              @CredentialRef, @StreamProfile, CAST(@ConfigJson AS jsonb), @Enabled)
            RETURNING source_id
            """,
            new
            {
                request.TenantId,
                request.CameraId,
                SourceCode = request.SourceCode.Trim(),
                SourceType = Default(request.SourceType, "rtsp"),
                UriTemplate = request.UriTemplate.Trim(),
                request.CredentialRef,
                StreamProfile = Default(request.StreamProfile, "sub"),
                ConfigJson = configJson,
                request.Enabled
            }, cancellationToken: cancellationToken));
    }

    public async Task<MediaSourceRecord?> GetSourceAsync(long sourceId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaSourceRecord>(Command(
            $"SELECT {SourceColumns} FROM media_source WHERE source_id=@SourceId",
            new { SourceId = sourceId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MediaAnalysisSubscriptionRecord>> ListSubscriptionsAsync(long? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var sql = tenantId.HasValue
            ? $"SELECT {SubscriptionColumns} FROM media_analysis_subscription WHERE tenant_id=@TenantId ORDER BY subscription_id DESC"
            : $"SELECT {SubscriptionColumns} FROM media_analysis_subscription ORDER BY subscription_id DESC";
        return (await connection.QueryAsync<MediaAnalysisSubscriptionRecord>(Command(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<MediaAnalysisSubscriptionRecord?> GetSubscriptionAsync(long subscriptionId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisSubscriptionRecord>(Command(
            $"SELECT {SubscriptionColumns} FROM media_analysis_subscription WHERE subscription_id=@SubscriptionId",
            new { SubscriptionId = subscriptionId }, cancellationToken: cancellationToken));
    }

    public async Task<long> UpsertSubscriptionAsync(long? subscriptionId, StreamSubscriptionUpsertRequest request, CancellationToken cancellationToken)
    {
        var clientId = string.IsNullOrWhiteSpace(request.ClientSubscriptionId)
            ? $"stream-{Guid.NewGuid():N}"
            : request.ClientSubscriptionId.Trim();
        var configJson = Json(request.Config);
        await using var connection = connectionFactory.CreateConnection();
        if (subscriptionId.HasValue)
        {
            return await connection.ExecuteScalarAsync<long>(Command(
                """
                UPDATE media_analysis_subscription SET tenant_id=@TenantId, provider_id=@ProviderId,
                  source_id=@SourceId, pipeline_id=@PipelineId, desired_state=@DesiredState,
                  desired_config_json=CAST(@ConfigJson AS jsonb), version=version+1,
                  next_retry_at=NOW(), updated_at=NOW()
                WHERE subscription_id=@SubscriptionId RETURNING subscription_id
                """,
                new
                {
                    SubscriptionId = subscriptionId.Value,
                    request.TenantId,
                    request.ProviderId,
                    request.SourceId,
                    request.PipelineId,
                    DesiredState = NormalizeDesiredState(request.DesiredState),
                    ConfigJson = configJson
                }, cancellationToken: cancellationToken));
        }

        return await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO media_analysis_subscription(tenant_id, provider_id, source_id, pipeline_id,
              client_subscription_id, desired_state, desired_config_json, next_retry_at)
            VALUES(@TenantId, @ProviderId, @SourceId, @PipelineId, @ClientId, @DesiredState,
              CAST(@ConfigJson AS jsonb), NOW())
            RETURNING subscription_id
            """,
            new
            {
                request.TenantId,
                request.ProviderId,
                request.SourceId,
                request.PipelineId,
                ClientId = clientId,
                DesiredState = NormalizeDesiredState(request.DesiredState),
                ConfigJson = configJson
            }, cancellationToken: cancellationToken));
    }

    public async Task<bool> SetSubscriptionDesiredStateAsync(long subscriptionId, string desiredState, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(Command(
            "UPDATE media_analysis_subscription SET desired_state=@DesiredState, next_retry_at=NOW(), version=version+1, updated_at=NOW() WHERE subscription_id=@SubscriptionId",
            new { SubscriptionId = subscriptionId, DesiredState = NormalizeDesiredState(desiredState) }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<MediaAnalysisSubscriptionRecord>> ClaimSubscriptionsAsync(int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<MediaAnalysisSubscriptionRecord>(Command(
            $"""
            WITH due AS (
              SELECT subscription_id FROM media_analysis_subscription
              WHERE next_retry_at IS NULL OR next_retry_at <= NOW()
              ORDER BY COALESCE(next_retry_at, created_at), subscription_id
              FOR UPDATE SKIP LOCKED LIMIT @Limit
            )
            UPDATE media_analysis_subscription s
            SET next_retry_at=NOW()+@Lease, last_reconciled_at=NOW(), updated_at=NOW()
            FROM due WHERE s.subscription_id=due.subscription_id
            RETURNING {string.Join(", ", SubscriptionColumns.Split(',').Select(x => "s." + x.Trim()))}
            """,
            new { Limit = Math.Clamp(limit, 1, 100), Lease = lease }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task MarkSubscriptionReconciledAsync(
        long subscriptionId,
        string observedState,
        string? externalId,
        string? appliedHash,
        DateTimeOffset? heartbeat,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_subscription SET observed_state=@ObservedState,
              external_subscription_id=COALESCE(@ExternalId, external_subscription_id),
              applied_config_hash=COALESCE(@AppliedHash, applied_config_hash),
              last_heartbeat_at=COALESCE(@Heartbeat, last_heartbeat_at), retry_count=0,
              next_retry_at=NOW()+INTERVAL '30 seconds', last_error=NULL, last_reconciled_at=NOW(), updated_at=NOW()
            WHERE subscription_id=@SubscriptionId
            """,
            new { SubscriptionId = subscriptionId, ObservedState = observedState, ExternalId = externalId, AppliedHash = appliedHash, Heartbeat = heartbeat }, cancellationToken: cancellationToken));
    }

    public async Task MarkSubscriptionFailureAsync(long subscriptionId, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_subscription SET observed_state='failed', retry_count=retry_count+1,
              next_retry_at=NOW()+(LEAST(300, POWER(2, LEAST(retry_count, 8))) * INTERVAL '1 second'),
              last_error=LEFT(@Error, 2000), last_reconciled_at=NOW(), updated_at=NOW()
            WHERE subscription_id=@SubscriptionId
            """,
            new { SubscriptionId = subscriptionId, Error = error }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MediaAnalysisJobRecord>> ListJobsAsync(long? tenantId, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<MediaAnalysisJobRecord>(Command(
            $"""
            SELECT {JobColumns} FROM media_analysis_job
            WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND (@Status IS NULL OR status=@Status)
            ORDER BY job_id DESC LIMIT @Limit
            """,
            new { TenantId = tenantId, Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(), Limit = Math.Clamp(limit, 1, 500) }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<MediaAnalysisJobRecord?> GetJobAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisJobRecord>(Command(
            $"SELECT {JobColumns} FROM media_analysis_job WHERE job_id=@JobId",
            new { JobId = jobId }, cancellationToken: cancellationToken));
    }

    public async Task<long> CreateJobAsync(AnalysisJobCreateRequest request, CancellationToken cancellationToken)
    {
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey.Trim();
        var requestJson = JsonSerializer.Serialize(new
        {
            protocol_version = "1.0",
            idempotency_key = idempotencyKey,
            media_type = request.MediaType,
            media_uri = request.MediaUri,
            source_id = request.SourceId,
            options = request.Options
        }, MediaAnalysisJson.Options);
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO media_analysis_job(tenant_id, provider_id, pipeline_id, source_id, idempotency_key,
              media_type, media_uri, request_json, status, next_retry_at)
            VALUES(@TenantId, @ProviderId, @PipelineId, @SourceId, @IdempotencyKey,
              @MediaType, @MediaUri, CAST(@RequestJson AS jsonb), 'pending', NOW())
            ON CONFLICT(provider_id, idempotency_key) DO UPDATE SET updated_at=media_analysis_job.updated_at
            RETURNING job_id
            """,
            new
            {
                request.TenantId,
                request.ProviderId,
                request.PipelineId,
                request.SourceId,
                IdempotencyKey = idempotencyKey,
                MediaType = request.MediaType.Trim().ToLowerInvariant(),
                request.MediaUri,
                RequestJson = requestJson
            }, cancellationToken: cancellationToken));
    }

    public async Task<MediaAnalysisJobRecord?> ClaimJobAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisJobRecord>(Command(
            $"""
            WITH due AS (
              SELECT job_id FROM media_analysis_job
              WHERE status IN ('pending','retry_wait') AND (next_retry_at IS NULL OR next_retry_at<=NOW())
              ORDER BY job_id FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE media_analysis_job j SET status='submitting', next_retry_at=NOW()+@Lease, updated_at=NOW()
            FROM due WHERE j.job_id=due.job_id
            RETURNING {string.Join(", ", JobColumns.Split(',').Select(x => "j." + x.Trim()))}
            """,
            new { Lease = lease }, cancellationToken: cancellationToken));
    }

    public async Task UpdateJobFromProviderAsync(long jobId, ProviderSubmission submission, JsonElement? result, CancellationToken cancellationToken)
    {
        var status = NormalizeJobState(submission.State);
        var resultJson = result.HasValue ? result.Value.GetRawText() : "{}";
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_job SET external_job_id=@ExternalId, status=@Status,
              result_json=CAST(@ResultJson AS jsonb), submitted_at=COALESCE(submitted_at,NOW()),
              started_at=CASE WHEN @Status IN ('running','completed') THEN COALESCE(started_at,NOW()) ELSE started_at END,
              completed_at=CASE WHEN @Status='completed' THEN NOW() ELSE completed_at END,
              progress=CASE WHEN @Status='completed' THEN 100 ELSE progress END,
              next_retry_at=NULL, error_code=NULL, error_message=NULL, updated_at=NOW()
            WHERE job_id=@JobId
            """,
            new { JobId = jobId, submission.ExternalId, Status = status, ResultJson = resultJson }, cancellationToken: cancellationToken));
    }

    public async Task MarkJobFailureAsync(long jobId, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_job SET status=CASE WHEN retry_count>=4 THEN 'failed' ELSE 'retry_wait' END,
              retry_count=retry_count+1,
              next_retry_at=CASE WHEN retry_count>=4 THEN NULL ELSE NOW()+(LEAST(300, POWER(2, retry_count)) * INTERVAL '1 second') END,
              error_code='provider_error', error_message=LEFT(@Error,2000), updated_at=NOW()
            WHERE job_id=@JobId
            """,
            new { JobId = jobId, Error = error }, cancellationToken: cancellationToken));
    }

    public async Task<bool> SetJobStatusAsync(long jobId, string status, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(Command(
            "UPDATE media_analysis_job SET status=@Status, next_retry_at=NOW(), updated_at=NOW() WHERE job_id=@JobId",
            new { JobId = jobId, Status = status }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> TryRegisterNonceAsync(long providerId, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var inserted = await connection.ExecuteScalarAsync<int?>(Command(
            """
            WITH expired AS (
              DELETE FROM integration_webhook_nonce
              WHERE ctid IN (SELECT ctid FROM integration_webhook_nonce WHERE expires_at<NOW() LIMIT 1000)
            )
            INSERT INTO integration_webhook_nonce(provider_id, nonce, expires_at)
            VALUES(@ProviderId,@Nonce,@ExpiresAt)
            ON CONFLICT DO NOTHING RETURNING 1
            """,
            new { ProviderId = providerId, Nonce = nonce, ExpiresAt = expiresAt }, cancellationToken: cancellationToken));
        return inserted == 1;
    }

    public async Task<MediaAnalysisWebhookResult> InsertInboxAsync(
        MediaAnalysisProviderRecord provider,
        IReadOnlyList<MediaAnalysisEventEnvelope> events,
        CancellationToken cancellationToken)
    {
        var accepted = 0;
        var duplicate = 0;
        var rejected = 0;
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in events)
        {
            if (!IsValidEnvelope(provider, item))
            {
                rejected++;
                continue;
            }

            var binding = await ResolveInboundBindingAsync(connection, transaction, provider, item, cancellationToken);
            if (binding is null)
            {
                rejected++;
                continue;
            }

            var payloadJson = JsonSerializer.Serialize(item, MediaAnalysisJson.Options);
            var hash = MediaAnalysisJson.Sha256(payloadJson);
            var inserted = await connection.ExecuteScalarAsync<int?>(Command(
                """
                INSERT INTO media_analysis_inbox(provider_id, tenant_id, event_id, provider_event_id,
                  subscription_id, source_id, sequence_no, schema_version, event_type, event_time,
                  produced_at, payload_json, payload_hash, trace_id)
                VALUES(@ProviderId,@TenantId,@EventId,@ProviderEventId,@SubscriptionId,@SourceId,@Sequence,
                  @SchemaVersion,@EventType,@EventTime,@ProducedAt,CAST(@PayloadJson AS jsonb),@PayloadHash,@TraceId)
                ON CONFLICT(provider_id,event_id) DO NOTHING RETURNING 1
                """,
                new
                {
                    provider.ProviderId,
                    binding.TenantId,
                    EventId = item.EventId.Trim(),
                    item.ProviderEventId,
                    binding.SubscriptionId,
                    binding.SourceId,
                    item.Sequence,
                    SchemaVersion = Default(item.SchemaVersion, "1.0"),
                    EventType = item.EventType.Trim(),
                    item.EventTime,
                    item.ProducedAt,
                    PayloadJson = payloadJson,
                    PayloadHash = hash,
                    item.TraceId
                }, transaction, cancellationToken));
            if (inserted == 1)
            {
                accepted++;
            }
            else
            {
                var existingHash = await connection.ExecuteScalarAsync<string?>(Command(
                    "SELECT payload_hash FROM media_analysis_inbox WHERE provider_id=@ProviderId AND event_id=@EventId",
                    new { provider.ProviderId, EventId = item.EventId.Trim() }, transaction, cancellationToken));
                if (string.Equals(existingHash, hash, StringComparison.Ordinal)) duplicate++; else rejected++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new MediaAnalysisWebhookResult(accepted, duplicate, rejected);
    }

    private static async Task<InboundBinding?> ResolveInboundBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MediaAnalysisProviderRecord provider,
        MediaAnalysisEventEnvelope item,
        CancellationToken cancellationToken)
    {
        var tenantId = await connection.ExecuteScalarAsync<long?>(Command(
            "SELECT tenant_id FROM tenant_project WHERE tenant_code=@TenantCode AND enabled=TRUE",
            new { TenantCode = item.TenantCode.Trim() }, transaction, cancellationToken));
        if (!tenantId.HasValue || (provider.TenantId.HasValue && provider.TenantId.Value != tenantId.Value))
        {
            return null;
        }

        long? subscriptionId = null;
        long? subscriptionSourceId = null;
        if (!string.IsNullOrWhiteSpace(item.SubscriptionId))
        {
            var subscription = await connection.QuerySingleOrDefaultAsync<InboundSubscription>(Command(
                "SELECT subscription_id AS SubscriptionId,source_id AS SourceId FROM media_analysis_subscription WHERE tenant_id=@TenantId AND provider_id=@ProviderId AND client_subscription_id=@ClientId",
                new { TenantId = tenantId.Value, provider.ProviderId, ClientId = item.SubscriptionId.Trim() }, transaction, cancellationToken));
            if (subscription is null) return null;
            subscriptionId = subscription.SubscriptionId;
            subscriptionSourceId = subscription.SourceId;
        }

        long? sourceId = null;
        if (!string.IsNullOrWhiteSpace(item.SourceId))
        {
            sourceId = await connection.ExecuteScalarAsync<long?>(Command(
                "SELECT source_id FROM media_source WHERE tenant_id=@TenantId AND source_code=@SourceCode AND enabled=TRUE",
                new { TenantId = tenantId.Value, SourceCode = item.SourceId.Trim() }, transaction, cancellationToken));
            if (!sourceId.HasValue) return null;
            if (subscriptionSourceId.HasValue && sourceId.Value != subscriptionSourceId.Value) return null;
        }
        else if (subscriptionSourceId.HasValue)
        {
            sourceId = subscriptionSourceId;
        }

        return new InboundBinding(tenantId.Value, subscriptionId, sourceId);
    }

    private static bool IsValidEnvelope(MediaAnalysisProviderRecord provider, MediaAnalysisEventEnvelope item)
    {
        if (string.IsNullOrWhiteSpace(item.EventId) || item.EventId.Trim().Length > 255
            || string.IsNullOrWhiteSpace(item.TenantCode) || item.TenantCode.Trim().Length > 64
            || string.IsNullOrWhiteSpace(item.EventType) || item.EventType.Trim().Length > 128
            || string.IsNullOrWhiteSpace(item.ProviderCode)
            || !string.Equals(item.ProviderCode.Trim(), provider.ProviderCode, StringComparison.OrdinalIgnoreCase)
            || item.Payload.ValueKind != JsonValueKind.Object
            || item.EventTime == default
            || item.EventTime > DateTimeOffset.UtcNow.AddMinutes(10)
            || item.Sequence is < 0)
        {
            return false;
        }

        var schemaVersion = Default(item.SchemaVersion, "1.0");
        return schemaVersion == "1" || schemaVersion.StartsWith("1.", StringComparison.Ordinal);
    }

    private static CommandDefinition Command(string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private static string Default(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string Json(JsonElement? value) => value.HasValue && value.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.Value.GetRawText() : "{}";

    private static string NormalizeDesiredState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "running" => "running",
        "stopped" => "stopped",
        _ => throw new ArgumentException("Desired state must be 'running' or 'stopped'.")
    };

    internal static string NormalizeJobState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "accepted" => "accepted",
        "running" or "started" => "running",
        "completed" or "succeeded" => "completed",
        "rejected" => "rejected",
        "cancelled" or "canceled" => "cancelled",
        "failed" => "failed",
        _ => "accepted"
    };

    private sealed record InboundBinding(long TenantId, long? SubscriptionId, long? SourceId);
    private sealed record InboundSubscription(long SubscriptionId, long SourceId);
}

internal static class MediaAnalysisJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string Sha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
