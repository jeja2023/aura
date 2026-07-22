using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.MediaAnalysis;

internal sealed class MediaAnalysisOrchestrator(
    MediaAnalysisRepository repository,
    PgSqlConnectionFactory connectionFactory,
    IMediaAnalysisProviderResolver providerResolver,
    IConfiguration configuration)
{
    public async Task<ProviderCapabilities> RefreshCapabilitiesAsync(long providerId, CancellationToken cancellationToken)
    {
        var provider = await RequireProviderAsync(providerId, cancellationToken);
        var capabilities = await providerResolver.Resolve(provider).GetCapabilitiesAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(capabilities.ProtocolVersion))
        {
            throw new InvalidDataException("Provider capability response has no protocol version.");
        }

        await repository.UpdateCapabilitiesAsync(providerId, capabilities, cancellationToken);
        return capabilities;
    }

    public async Task SubmitJobAsync(MediaAnalysisJobRecord job, CancellationToken cancellationToken)
    {
        var provider = await RequireProviderAsync(job.ProviderId, cancellationToken);
        var pipeline = await repository.GetPipelineAsync(job.PipelineId, cancellationToken)
            ?? throw new InvalidOperationException("Analysis pipeline does not exist.");
        if (pipeline.ProviderId != provider.ProviderId || !pipeline.Enabled)
        {
            throw new InvalidOperationException("Analysis pipeline is disabled or belongs to another provider.");
        }

        EnsureCapability(provider, job.MediaType == "image" ? "image.sync" : "video.async");
        using var requestDocument = JsonDocument.Parse(job.RequestJson);
        var adapter = providerResolver.Resolve(provider);
        var submission = job.MediaType == "image"
            ? await adapter.AnalyzeImageAsync(requestDocument.RootElement, cancellationToken)
            : await adapter.SubmitVideoAsync(requestDocument.RootElement, cancellationToken);
        await repository.UpdateJobFromProviderAsync(job.JobId, submission, submission.Result, cancellationToken);
        MediaAnalysisMetrics.ObserveJobOutcome(job, MediaAnalysisRepository.NormalizeJobState(submission.State));
        if (job.MediaType == "image" && submission.Result.HasValue
            && MediaAnalysisRepository.NormalizeJobState(submission.State) == "completed")
        {
            await PublishSynchronousImageResultAsync(job, provider, submission, cancellationToken);
        }
    }

    public async Task CancelJobAsync(long jobId, CancellationToken cancellationToken)
    {
        var job = await repository.GetJobAsync(jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Analysis job does not exist.");
        if (string.IsNullOrWhiteSpace(job.ExternalJobId))
        {
            await repository.SetJobStatusAsync(jobId, "cancelled", cancellationToken);
            MediaAnalysisMetrics.ObserveJobOutcome(job, "cancelled");
            return;
        }

        var provider = await RequireProviderAsync(job.ProviderId, cancellationToken);
        await repository.SetJobStatusAsync(jobId, "cancelling", cancellationToken);
        await providerResolver.Resolve(provider).CancelJobAsync(job.ExternalJobId, cancellationToken);
        await repository.SetJobStatusAsync(jobId, "cancelled", cancellationToken);
        MediaAnalysisMetrics.ObserveJobOutcome(job, "cancelled");
    }

    public async Task ReconcileSubscriptionAsync(MediaAnalysisSubscriptionRecord subscription, CancellationToken cancellationToken)
    {
        var provider = await RequireProviderAsync(subscription.ProviderId, cancellationToken);
        var source = await repository.GetSourceAsync(subscription.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Media source does not exist.");
        var pipeline = await repository.GetPipelineAsync(subscription.PipelineId, cancellationToken)
            ?? throw new InvalidOperationException("Analysis pipeline does not exist.");
        if (!source.Enabled || !pipeline.Enabled || pipeline.ProviderId != provider.ProviderId || source.TenantId != subscription.TenantId)
        {
            throw new InvalidOperationException("Subscription references a disabled or incompatible source/pipeline.");
        }

        EnsureCapability(provider, "stream.subscription");
        var adapter = providerResolver.Resolve(provider);
        if (subscription.DesiredState == "stopped")
        {
            if (subscription.ObservedState is not "stopped" and not "unknown")
            {
                await adapter.StopStreamAsync(subscription.ClientSubscriptionId, cancellationToken);
            }

            await repository.MarkSubscriptionReconciledAsync(
                subscription.SubscriptionId, "stopped", subscription.ExternalSubscriptionId, null, null, cancellationToken);
            return;
        }

        var tenantCode = await GetTenantCodeAsync(subscription.TenantId, cancellationToken);
        using var configDocument = JsonDocument.Parse(subscription.DesiredConfigJson);
        using var defaultsDocument = JsonDocument.Parse(pipeline.DefaultOptionsJson);
        var request = JsonSerializer.SerializeToElement(new
        {
            protocol_version = provider.ProtocolVersion,
            tenant_code = tenantCode,
            client_subscription_id = subscription.ClientSubscriptionId,
            source = new
            {
                source_id = source.SourceCode,
                type = source.SourceType,
                uri = source.UriTemplate,
                credential_ref = source.CredentialRef,
                stream_profile = source.StreamProfile
            },
            pipeline = new
            {
                code = pipeline.PipelineCode,
                model_version = pipeline.ModelVersion,
                options = configDocument.RootElement.ValueKind == JsonValueKind.Object
                    ? configDocument.RootElement
                    : defaultsDocument.RootElement
            },
            delivery = new
            {
                mode = "webhook",
                endpoint = configuration["MediaAnalysis:PublicWebhookUrl"]
            }
        }, MediaAnalysisJson.Options);
        var configHash = MediaAnalysisJson.Sha256(request.GetRawText());

        ProviderObservedState observed;
        if (subscription.AppliedConfigHash == configHash && subscription.ObservedState is "running" or "degraded")
        {
            observed = await adapter.GetStreamAsync(subscription.ClientSubscriptionId, cancellationToken);
        }
        else
        {
            observed = await adapter.UpsertStreamAsync(subscription.ClientSubscriptionId, request, cancellationToken);
        }

        var state = NormalizeStreamState(observed.State);
        await repository.MarkSubscriptionReconciledAsync(
            subscription.SubscriptionId,
            state,
            observed.ExternalId,
            configHash,
            state is "running" or "degraded" ? DateTimeOffset.UtcNow : null,
            cancellationToken);
    }

    private async Task<MediaAnalysisProviderRecord> RequireProviderAsync(long providerId, CancellationToken cancellationToken)
    {
        var provider = await repository.GetProviderAsync(providerId, cancellationToken)
            ?? throw new KeyNotFoundException("Media-analysis provider does not exist.");
        if (!provider.Enabled)
        {
            throw new InvalidOperationException("Media-analysis provider is disabled.");
        }
        return provider;
    }

    private async Task<string> GetTenantCodeAsync(long tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT tenant_code FROM tenant_project WHERE tenant_id=@TenantId AND enabled=TRUE",
            new { TenantId = tenantId }, cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("Tenant does not exist or is disabled.");
    }

    private async Task PublishSynchronousImageResultAsync(
        MediaAnalysisJobRecord job,
        MediaAnalysisProviderRecord provider,
        ProviderSubmission submission,
        CancellationToken cancellationToken)
    {
        var payload = JsonNode.Parse(submission.Result!.Value.GetRawText()) as JsonObject ?? new JsonObject();
        payload["job_id"] = submission.ExternalId;
        if (!string.IsNullOrWhiteSpace(job.MediaUri) && !payload.ContainsKey("image_url"))
            payload["image_url"] = job.MediaUri;
        var source = job.SourceId.HasValue
            ? await repository.GetSourceAsync(job.SourceId.Value, cancellationToken)
            : null;
        var now = DateTimeOffset.UtcNow;
        var envelope = new MediaAnalysisEventEnvelope(
            "1.0",
            $"job-{job.JobId}-image-result",
            submission.ExternalId,
            await GetTenantCodeAsync(job.TenantId, cancellationToken),
            provider.ProviderCode,
            null,
            source?.SourceCode,
            null,
            "object.detected",
            now,
            now,
            System.Diagnostics.Activity.Current?.TraceId.ToString(),
            JsonSerializer.SerializeToElement(payload, MediaAnalysisJson.Options));
        var result = await repository.InsertInboxAsync(provider, [envelope], cancellationToken);
        if (result.Accepted + result.Duplicate != 1)
            throw new InvalidOperationException("Synchronous image result could not be written to the media-analysis Inbox.");
    }

    private static void EnsureCapability(MediaAnalysisProviderRecord provider, string required)
    {
        if (string.IsNullOrWhiteSpace(provider.CapabilitiesJson) || provider.CapabilitiesJson == "{}")
        {
            throw new InvalidOperationException("Provider capabilities have not been discovered.");
        }

        using var document = JsonDocument.Parse(provider.CapabilitiesJson);
        if (!document.RootElement.TryGetProperty("capabilities", out var values)
            || values.ValueKind != JsonValueKind.Array
            || !values.EnumerateArray().Any(x => string.Equals(x.GetString(), required, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Provider does not declare required capability '{required}'.");
        }
    }

    private static string NormalizeStreamState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "starting" => "starting",
        "running" => "running",
        "degraded" => "degraded",
        "stopping" => "stopping",
        "stopped" => "stopped",
        "failed" => "failed",
        _ => "unknown"
    };
}
