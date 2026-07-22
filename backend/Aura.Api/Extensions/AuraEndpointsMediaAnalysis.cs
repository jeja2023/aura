using System.Text.Json;
using Aura.Api.Internal;
using Aura.Api.MediaAnalysis;
using Aura.Api.Ops;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aura.Api.Extensions;

internal static class AuraEndpointsMediaAnalysis
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var management = app.MapGroup("/api/media-analysis").WithTags("Media analysis");

        management.MapGet("/tenants", async (HttpContext http, TenantScopeAccessService access, CancellationToken ct) =>
            Ok(await access.ListAsync(http.User, ct)))
            .RequireAuthorization("MediaAnalysisView");

        management.MapGet("/providers", async (HttpContext http, long? tenantId, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, allowGlobalForNonSuper: false, ct);
            return denied ?? Ok(await repository.ListProvidersAsync(tenantId, ct));
        }).RequireAuthorization("MediaAnalysisView");

        management.MapPost("/providers", async (HttpContext http, ProviderUpsertRequest request, MediaAnalysisRepository repository, MediaAnalysisOutboundUrlPolicy urlPolicy, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, request.TenantId, access, allowGlobalForNonSuper: false, ct);
            if (denied is not null) return denied;
            var error = await ValidateProviderAsync(request, urlPolicy, ct);
            if (error is not null) return error;
            var id = await repository.UpsertProviderAsync(null, request, ct);
            return Results.Created($"/api/media-analysis/providers/{id}", new { code = 0, msg = "created", data = new { providerId = id } });
        }).RequireAuthorization("MediaAnalysisManage");

        management.MapPut("/providers/{id:long}", async (HttpContext http, long id, ProviderUpsertRequest request, MediaAnalysisRepository repository, MediaAnalysisOutboundUrlPolicy urlPolicy, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var existing = await repository.GetProviderAsync(id, ct);
            if (existing is null) return AuraApiResults.NotFound("Provider not found.");
            var denied = await DenyTenantAsync(http, existing.TenantId, access, allowGlobalForNonSuper: false, ct)
                ?? await DenyTenantAsync(http, request.TenantId, access, allowGlobalForNonSuper: false, ct);
            if (denied is not null) return denied;
            var error = await ValidateProviderAsync(request, urlPolicy, ct);
            if (error is not null) return error;
            var updated = await repository.UpsertProviderAsync(id, request, ct);
            return Ok(new { providerId = updated });
        }).RequireAuthorization("MediaAnalysisManage");

        management.MapPost("/providers/{id:long}/test", async (HttpContext http, long id, MediaAnalysisRepository repository, MediaAnalysisOrchestrator orchestrator, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var provider = await repository.GetProviderAsync(id, ct);
            if (provider is null) return AuraApiResults.NotFound("Provider not found.");
            var denied = await DenyTenantAsync(http, provider.TenantId, access, allowGlobalForNonSuper: true, ct);
            return denied ?? Ok(await orchestrator.RefreshCapabilitiesAsync(id, ct));
        })
            .RequireAuthorization("MediaAnalysisOperate");

        management.MapGet("/providers/{id:long}/capabilities", async (HttpContext http, long id, MediaAnalysisRepository repository, MediaAnalysisOrchestrator orchestrator, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var provider = await repository.GetProviderAsync(id, ct);
            if (provider is null) return AuraApiResults.NotFound("Provider not found.");
            var denied = await DenyTenantAsync(http, provider.TenantId, access, allowGlobalForNonSuper: true, ct);
            return denied ?? Ok(await orchestrator.RefreshCapabilitiesAsync(id, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapGet("/pipelines", async (HttpContext http, long? providerId, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (!providerId.HasValue && !TenantScopeAccessService.IsSuperAdmin(http.User))
                return AuraApiResults.Forbidden("providerId is required within a tenant scope.");
            if (providerId.HasValue)
            {
                var provider = await repository.GetProviderAsync(providerId.Value, ct);
                if (provider is null) return AuraApiResults.NotFound("Provider not found.");
                var denied = await DenyTenantAsync(http, provider.TenantId, access, allowGlobalForNonSuper: true, ct);
                if (denied is not null) return denied;
            }
            return Ok(await repository.ListPipelinesAsync(providerId, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapPost("/pipelines", async (HttpContext http, PipelineCreateRequest request, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (request.ProviderId <= 0 || string.IsNullOrWhiteSpace(request.PipelineCode) || string.IsNullOrWhiteSpace(request.DisplayName))
                return AuraApiResults.BadRequest("providerId, pipelineCode and displayName are required.");
            var provider = await repository.GetProviderAsync(request.ProviderId, ct);
            if (provider is null) return AuraApiResults.NotFound("Provider not found.");
            var denied = await DenyTenantAsync(http, provider.TenantId, access, allowGlobalForNonSuper: false, ct);
            if (denied is not null) return denied;
            var id = await repository.UpsertPipelineAsync(null, request, ct);
            return Results.Created($"/api/media-analysis/pipelines/{id}", new { code = 0, msg = "created", data = new { pipelineId = id } });
        }).RequireAuthorization("MediaAnalysisManage");

        management.MapPut("/pipelines/{id:long}", async (HttpContext http, long id, PipelineCreateRequest request, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var existing = await repository.GetPipelineAsync(id, ct);
            var provider = await repository.GetProviderAsync(request.ProviderId, ct);
            if (existing is null || provider is null) return AuraApiResults.NotFound("Pipeline or provider not found.");
            var existingProvider = await repository.GetProviderAsync(existing.ProviderId, ct);
            var denied = await DenyTenantAsync(http, existingProvider?.TenantId, access, allowGlobalForNonSuper: false, ct)
                ?? await DenyTenantAsync(http, provider.TenantId, access, allowGlobalForNonSuper: false, ct);
            if (denied is not null) return denied;
            return Ok(new { pipelineId = await repository.UpsertPipelineAsync(id, request, ct) });
        }).RequireAuthorization("MediaAnalysisManage");

        management.MapGet("/sources", async (HttpContext http, long? tenantId, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, allowGlobalForNonSuper: false, ct);
            return denied ?? Ok(await repository.ListSourcesAsync(tenantId, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapPost("/sources", async (HttpContext http, MediaSourceCreateRequest request, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (request.TenantId <= 0 || request.CameraId <= 0 || string.IsNullOrWhiteSpace(request.SourceCode) || string.IsNullOrWhiteSpace(request.UriTemplate))
                return AuraApiResults.BadRequest("tenantId, cameraId, sourceCode and uriTemplate are required.");
            var secretError = ValidateOptionalSecretReference(request.CredentialRef, "credentialRef");
            if (secretError is not null) return secretError;
            var denied = await DenyTenantAsync(http, request.TenantId, access, allowGlobalForNonSuper: false, ct);
            if (denied is not null) return denied;
            var id = await repository.UpsertSourceAsync(null, request, ct);
            return Results.Created($"/api/media-analysis/sources/{id}", new { code = 0, msg = "created", data = new { sourceId = id } });
        }).RequireAuthorization("MediaAnalysisManage");

        management.MapPut("/sources/{id:long}", async (HttpContext http, long id, MediaSourceCreateRequest request, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var existing = await repository.GetSourceAsync(id, ct);
            if (existing is null) return AuraApiResults.NotFound("Source not found.");
            var secretError = ValidateOptionalSecretReference(request.CredentialRef, "credentialRef");
            if (secretError is not null) return secretError;
            var denied = await DenyTenantAsync(http, existing.TenantId, access, false, ct)
                ?? await DenyTenantAsync(http, request.TenantId, access, false, ct);
            return denied ?? Ok(new { sourceId = await repository.UpsertSourceAsync(id, request, ct) });
        })
            .RequireAuthorization("MediaAnalysisManage");

        management.MapGet("/subscriptions", async (HttpContext http, long? tenantId, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, false, ct);
            return denied ?? Ok(await repository.ListSubscriptionsAsync(tenantId, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapGet("/subscriptions/{id:long}", async (HttpContext http, long id, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var item = await repository.GetSubscriptionAsync(id, ct);
            if (item is null) return AuraApiResults.NotFound("Subscription not found.");
            var denied = await DenyTenantAsync(http, item.TenantId, access, false, ct);
            return denied ?? Ok(item);
        }).RequireAuthorization("MediaAnalysisView");

        management.MapPut("/subscriptions/{id:long}", async (HttpContext http, long id, StreamSubscriptionUpsertRequest request, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, request.TenantId, access, false, ct);
            if (denied is not null) return denied;
            if (id != 0)
            {
                var existing = await repository.GetSubscriptionAsync(id, ct);
                if (existing is null) return AuraApiResults.NotFound("Subscription not found.");
                denied = await DenyTenantAsync(http, existing.TenantId, access, false, ct);
                if (denied is not null) return denied;
            }
            if (!await repository.IsValidBindingAsync(
                    request.TenantId, request.ProviderId, request.PipelineId, request.SourceId, "stream", ct))
                return AuraApiResults.BadRequest("Provider, pipeline and source must be enabled and belong to the requested tenant.");
            return Ok(new { subscriptionId = await repository.UpsertSubscriptionAsync(id == 0 ? null : id, request, ct) });
        })
            .RequireAuthorization("MediaAnalysisManage");

        management.MapDelete("/subscriptions/{id:long}", async (HttpContext http, long id, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var item = await repository.GetSubscriptionAsync(id, ct);
            if (item is null) return AuraApiResults.NotFound("Subscription not found.");
            var denied = await DenyTenantAsync(http, item.TenantId, access, false, ct);
            if (denied is not null) return denied;
            return await repository.SetSubscriptionDesiredStateAsync(id, "stopped", ct)
                ? Ok(new { subscriptionId = id, desiredState = "stopped" })
                : AuraApiResults.NotFound("Subscription not found.");
        })
            .RequireAuthorization("MediaAnalysisManage");

        management.MapPost("/subscriptions/{id:long}/reconcile", async (HttpContext http, long id, MediaAnalysisRepository repository, MediaAnalysisOrchestrator orchestrator, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var item = await repository.GetSubscriptionAsync(id, ct);
            if (item is null) return AuraApiResults.NotFound("Subscription not found.");
            var denied = await DenyTenantAsync(http, item.TenantId, access, false, ct);
            if (denied is not null) return denied;
            await orchestrator.ReconcileSubscriptionAsync(item, ct);
            return Ok(await repository.GetSubscriptionAsync(id, ct));
        }).RequireAuthorization("MediaAnalysisOperate");

        management.MapPost("/jobs", async (HttpContext http, AnalysisJobCreateRequest request, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            if (request.TenantId <= 0 || request.ProviderId <= 0 || request.PipelineId <= 0 || request.MediaType is not ("image" or "video"))
                return AuraApiResults.BadRequest("tenantId, providerId, pipelineId and mediaType(image|video) are required.");
            if (string.IsNullOrWhiteSpace(request.MediaUri) && !request.SourceId.HasValue)
                return AuraApiResults.BadRequest("mediaUri or sourceId is required.");
            var denied = await DenyTenantAsync(http, request.TenantId, access, false, ct);
            if (denied is not null) return denied;
            if (!await repository.IsValidBindingAsync(
                    request.TenantId, request.ProviderId, request.PipelineId, request.SourceId, request.MediaType, ct))
                return AuraApiResults.BadRequest("Provider, pipeline and source must be enabled, compatible and belong to the requested tenant.");
            var id = await repository.CreateJobAsync(request, ct);
            return Results.Accepted($"/api/media-analysis/jobs/{id}", new { code = 0, msg = "accepted", data = new { jobId = id } });
        }).RequireAuthorization("MediaAnalysisOperate");

        management.MapGet("/jobs", async (HttpContext http, long? tenantId, string? status, int? limit, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, false, ct);
            return denied ?? Ok(await repository.ListJobsAsync(tenantId, status, limit ?? 100, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapGet("/jobs/{id:long}", async (HttpContext http, long id, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var item = await repository.GetJobAsync(id, ct);
            if (item is null) return AuraApiResults.NotFound("Job not found.");
            var denied = await DenyTenantAsync(http, item.TenantId, access, false, ct);
            return denied ?? Ok(item);
        }).RequireAuthorization("MediaAnalysisView");

        management.MapPost("/jobs/{id:long}/cancel", async (HttpContext http, long id, MediaAnalysisRepository repository, MediaAnalysisOrchestrator orchestrator, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var item = await repository.GetJobAsync(id, ct);
            if (item is null) return AuraApiResults.NotFound("Job not found.");
            var denied = await DenyTenantAsync(http, item.TenantId, access, false, ct);
            if (denied is not null) return denied;
            await orchestrator.CancelJobAsync(id, ct);
            return Ok(new { jobId = id, status = "cancelled" });
        }).RequireAuthorization("MediaAnalysisOperate");

        management.MapPost("/jobs/{id:long}/retry", async (HttpContext http, long id, MediaAnalysisRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var item = await repository.GetJobAsync(id, ct);
            if (item is null) return AuraApiResults.NotFound("Job not found.");
            var denied = await DenyTenantAsync(http, item.TenantId, access, false, ct);
            if (denied is not null) return denied;
            return await repository.SetJobStatusAsync(id, "retry_wait", ct)
                ? Ok(new { jobId = id, status = "retry_wait" })
                : AuraApiResults.NotFound("Job not found.");
        })
            .RequireAuthorization("MediaAnalysisOperate");

        management.MapGet("/ops/inbox", async (HttpContext http, long? tenantId, string? status, int? limit, InboxRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, false, ct);
            return denied ?? Ok(await repository.QueryAsync(tenantId, status, limit ?? 100, ct));
        })
            .RequireAuthorization("MediaAnalysisReplay");

        management.MapGet("/ops/inbox/stats", async (HttpContext http, long? tenantId, InboxRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, false, ct);
            return denied ?? Ok(await repository.GetStatsAsync(tenantId, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapGet("/ops/readiness", async (HttpContext http, MediaPlatformReadinessService readiness, CancellationToken ct) =>
            TenantScopeAccessService.IsSuperAdmin(http.User)
                ? Ok(await readiness.GetAsync(ct))
                : AuraApiResults.Forbidden("Platform-wide readiness requires a global administrator."))
            .RequireAuthorization("MediaAnalysisView");

        management.MapPost("/ops/inbox/replay", async (HttpContext http, ReplayRequest request, InboxRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, request.TenantId, access, false, ct);
            return denied ?? Ok(new { replayed = await repository.ReplayAsync(request, ct) });
        })
            .RequireAuthorization("MediaAnalysisReplay");

        management.MapGet("/ops/artifacts", async (HttpContext http, long? tenantId, int? limit, MediaArtifactRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, tenantId, access, false, ct);
            return denied ?? Ok(await repository.GetStatusAsync(tenantId, limit ?? 100, ct));
        })
            .RequireAuthorization("MediaAnalysisView");

        management.MapPost("/ops/artifacts/replay", async (HttpContext http, ArtifactReplayRequest request, MediaArtifactRepository repository, TenantScopeAccessService access, CancellationToken ct) =>
        {
            var denied = await DenyTenantAsync(http, request.TenantId, access, false, ct);
            return denied ?? Ok(new { replayed = await repository.ReplayAsync(request.TenantId, request.ArtifactIds, request.Limit, ct) });
        })
            .RequireAuthorization("MediaAnalysisReplay");

        var integration = app.MapGroup("/api/integrations/media-analysis/v1").WithTags("Media analysis integration");
        integration.MapPost("/events", (HttpRequest request, MediaAnalysisWebhookVerifier verifier, MediaAnalysisRepository repository, IConfiguration configuration, CancellationToken ct) =>
            ReceiveSingleAsync(request, verifier, repository, configuration, ct));
        integration.MapPost("/events/batch", (HttpRequest request, MediaAnalysisWebhookVerifier verifier, MediaAnalysisRepository repository, IConfiguration configuration, CancellationToken ct) =>
            ReceiveBatchAsync(request, verifier, repository, configuration, ct));
        integration.MapGet("/health", (HttpRequest request, MediaAnalysisWebhookVerifier verifier, CancellationToken ct) =>
            IntegrationHealthAsync(request, verifier, ct));
    }

    private static async Task<IResult> ReceiveSingleAsync(
        HttpRequest request,
        MediaAnalysisWebhookVerifier verifier,
        MediaAnalysisRepository repository,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadBodyAsync(request, configuration, cancellationToken);
            var provider = await verifier.VerifyAsync(request, body, cancellationToken);
            var item = JsonSerializer.Deserialize<MediaAnalysisEventEnvelope>(body.Span, MediaAnalysisJson.Options);
            if (item is null) return AuraApiResults.BadRequest("Invalid event envelope.");
            var result = await repository.InsertInboxAsync(provider, [item], cancellationToken);
            MediaAnalysisMetrics.ObserveWebhook(provider.ProviderCode, result);
            return Ok(result);
        }
        catch (WebhookAuthenticationException ex)
        {
            MediaAnalysisMetrics.ObserveAuthenticationFailure(ex);
            return AuraApiResults.Unauthorized(ex.Message, 40120);
        }
        catch (JsonException ex)
        {
            return AuraApiResults.BadRequest("Invalid event JSON.", 40020, new { error = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return AuraApiResults.BadRequest(ex.Message, 40021);
        }
    }

    private static async Task<IResult> ReceiveBatchAsync(
        HttpRequest request,
        MediaAnalysisWebhookVerifier verifier,
        MediaAnalysisRepository repository,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadBodyAsync(request, configuration, cancellationToken);
            var provider = await verifier.VerifyAsync(request, body, cancellationToken);
            var batch = JsonSerializer.Deserialize<MediaAnalysisWebhookBatch>(body.Span, MediaAnalysisJson.Options);
            if (batch?.Events is null) return AuraApiResults.BadRequest("Invalid event batch.");
            var maxBatch = Math.Clamp(configuration.GetValue("MediaAnalysis:Webhook:MaxBatchSize", 200), 1, 1000);
            if (batch.Events.Count == 0 || batch.Events.Count > maxBatch)
                return AuraApiResults.BadRequest($"Batch must contain between 1 and {maxBatch} events.");
            var result = await repository.InsertInboxAsync(provider, batch.Events, cancellationToken);
            MediaAnalysisMetrics.ObserveWebhook(provider.ProviderCode, result);
            return Ok(result);
        }
        catch (WebhookAuthenticationException ex)
        {
            MediaAnalysisMetrics.ObserveAuthenticationFailure(ex);
            return AuraApiResults.Unauthorized(ex.Message, 40120);
        }
        catch (JsonException ex)
        {
            return AuraApiResults.BadRequest("Invalid event JSON.", 40020, new { error = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return AuraApiResults.BadRequest(ex.Message, 40021);
        }
    }

    private static async Task<IResult> IntegrationHealthAsync(HttpRequest request, MediaAnalysisWebhookVerifier verifier, CancellationToken cancellationToken)
    {
        try
        {
            var provider = await verifier.VerifyAsync(request, ReadOnlyMemory<byte>.Empty, cancellationToken);
            return Ok(new { status = "ok", protocolVersion = provider.ProtocolVersion, serverTime = DateTimeOffset.UtcNow });
        }
        catch (WebhookAuthenticationException ex)
        {
            return AuraApiResults.Unauthorized(ex.Message, 40120);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpRequest request, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Clamp(configuration.GetValue("MediaAnalysis:Webhook:MaxBodyBytes", 2 * 1024 * 1024), 1024, 16 * 1024 * 1024);
        if (request.ContentLength > maxBytes)
        {
            throw new InvalidDataException($"Request body exceeds {maxBytes} bytes.");
        }

        await using var buffer = new MemoryStream(Math.Min(maxBytes, (int)(request.ContentLength ?? 0)));
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException($"Request body exceeds {maxBytes} bytes.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static async Task<IResult?> ValidateProviderAsync(
        ProviderUpsertRequest request,
        MediaAnalysisOutboundUrlPolicy urlPolicy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderCode) || string.IsNullOrWhiteSpace(request.DisplayName))
            return AuraApiResults.BadRequest("providerCode and displayName are required.");
        var authType = string.IsNullOrWhiteSpace(request.AuthType) ? "hmac" : request.AuthType.Trim().ToLowerInvariant();
        if (authType is not ("none" or "hmac" or "bearer" or "oauth2_client" or "mtls"))
            return AuraApiResults.BadRequest("authType must be none, hmac, bearer, oauth2_client or mtls.");
        if (authType is "hmac" or "bearer" or "oauth2_client" && string.IsNullOrWhiteSpace(request.SecretRef))
            return AuraApiResults.BadRequest("secretRef is required for the selected authentication type.");
        var webhookAuthType = string.IsNullOrWhiteSpace(request.WebhookAuthType)
            ? "hmac"
            : request.WebhookAuthType.Trim().ToLowerInvariant();
        if (webhookAuthType != "hmac")
            return AuraApiResults.BadRequest("webhookAuthType must be hmac.");
        if (string.IsNullOrWhiteSpace(request.WebhookSecretRef))
            return AuraApiResults.BadRequest("webhookSecretRef is required for authenticated event delivery.");
        var secretError = ValidateOptionalSecretReference(request.SecretRef, "secretRef")
            ?? ValidateOptionalSecretReference(request.WebhookSecretRef, "webhookSecretRef");
        if (secretError is not null) return secretError;
        try
        {
            var baseUri = await urlPolicy.ValidateAsync(request.BaseUrl, cancellationToken);
            if (authType == "mtls" && baseUri.Scheme != Uri.UriSchemeHttps)
                return AuraApiResults.BadRequest("mTLS providers require an HTTPS baseUrl.");
        }
        catch (InvalidDataException ex)
        {
            return AuraApiResults.BadRequest(ex.Message);
        }
        return null;
    }

    private static IResult? ValidateOptionalSecretReference(string? secretReference, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(secretReference)) return null;
        try
        {
            SecretReferenceValidator.Validate(secretReference);
            return null;
        }
        catch (InvalidDataException ex)
        {
            return AuraApiResults.BadRequest($"{fieldName}: {ex.Message}");
        }
    }

    private static async Task<IResult?> DenyTenantAsync(
        HttpContext http,
        long? tenantId,
        TenantScopeAccessService access,
        bool allowGlobalForNonSuper,
        CancellationToken cancellationToken)
    {
        if (!tenantId.HasValue)
        {
            return TenantScopeAccessService.IsSuperAdmin(http.User) || allowGlobalForNonSuper
                ? null
                : AuraApiResults.Forbidden("An explicit authorized tenantId is required.");
        }
        return await access.CanAccessAsync(http.User, tenantId.Value, cancellationToken)
            ? null
            : AuraApiResults.Forbidden("The requested tenant is outside the current user's scope.");
    }

    private static IResult Ok(object? data) => Results.Ok(new { code = 0, msg = "ok", data });
}
