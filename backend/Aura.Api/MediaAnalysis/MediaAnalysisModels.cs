using System.Text.Json;

namespace Aura.Api.MediaAnalysis;

internal sealed record MediaAnalysisProviderRecord(
    long ProviderId,
    long? TenantId,
    string ProviderCode,
    string DisplayName,
    string AdapterType,
    string BaseUrl,
    string AuthType,
    string? SecretRef,
    string WebhookAuthType,
    string? WebhookSecretRef,
    string CapabilitiesJson,
    string ProtocolVersion,
    int TimeoutSeconds,
    int MaxConcurrency,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record MediaAnalysisPipelineRecord(
    long PipelineId,
    long ProviderId,
    string PipelineCode,
    string DisplayName,
    string ModelName,
    string ModelVersion,
    string InputTypesJson,
    string OutputTypesJson,
    int? EmbeddingDimension,
    string DefaultOptionsJson,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record MediaSourceRecord(
    long SourceId,
    long TenantId,
    long CameraId,
    string SourceCode,
    string SourceType,
    string UriTemplate,
    string? CredentialRef,
    string StreamProfile,
    string ConfigJson,
    long ConfigVersion,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record MediaAnalysisSubscriptionRecord(
    long SubscriptionId,
    long TenantId,
    long ProviderId,
    long SourceId,
    long PipelineId,
    string ClientSubscriptionId,
    string? ExternalSubscriptionId,
    string DesiredState,
    string ObservedState,
    string DesiredConfigJson,
    string? AppliedConfigHash,
    DateTime? LastHeartbeatAt,
    DateTime? LastReconciledAt,
    int RetryCount,
    DateTime? NextRetryAt,
    string? LastError,
    long Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record MediaAnalysisJobRecord(
    long JobId,
    long TenantId,
    long ProviderId,
    long PipelineId,
    long? SourceId,
    string IdempotencyKey,
    string? ExternalJobId,
    string MediaType,
    string? MediaUri,
    string RequestJson,
    string ResultJson,
    string Status,
    decimal Progress,
    int RetryCount,
    DateTime? NextRetryAt,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime? SubmittedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record MediaAnalysisInboxRecord(
    long InboxId,
    long ProviderId,
    long TenantId,
    string EventId,
    string? ProviderEventId,
    long? SubscriptionId,
    long? SourceId,
    long? SequenceNo,
    string SchemaVersion,
    string EventType,
    DateTime EventTime,
    DateTime? ProducedAt,
    DateTime ReceivedAt,
    string PayloadJson,
    string PayloadHash,
    string Status,
    int AttemptCount,
    string? TraceId);

internal sealed record IntegrationOutboxRecord(
    long OutboxId,
    long? TenantId,
    string AggregateType,
    string AggregateId,
    string EventType,
    string PayloadJson,
    int AttemptCount,
    DateTime CreatedAt);

internal sealed record ProviderCapabilities(
    string ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ProviderPipelineCapability> Pipelines);

internal sealed record ProviderPipelineCapability(
    string Code,
    IReadOnlyList<string> Models,
    int? EmbeddingDimension);

internal sealed record ProviderUpsertRequest(
    long? TenantId,
    string ProviderCode,
    string DisplayName,
    string? AdapterType,
    string BaseUrl,
    string? AuthType,
    string? SecretRef,
    string? WebhookAuthType,
    string? WebhookSecretRef,
    string? ProtocolVersion,
    int? TimeoutSeconds,
    int? MaxConcurrency,
    bool Enabled);

internal sealed record PipelineCreateRequest(
    long ProviderId,
    string PipelineCode,
    string DisplayName,
    string? ModelName,
    string? ModelVersion,
    IReadOnlyList<string>? InputTypes,
    IReadOnlyList<string>? OutputTypes,
    int? EmbeddingDimension,
    JsonElement? DefaultOptions,
    bool Enabled);

internal sealed record MediaSourceCreateRequest(
    long TenantId,
    long CameraId,
    string SourceCode,
    string? SourceType,
    string UriTemplate,
    string? CredentialRef,
    string? StreamProfile,
    JsonElement? Config,
    bool Enabled);

internal sealed record StreamSubscriptionUpsertRequest(
    long TenantId,
    long ProviderId,
    long SourceId,
    long PipelineId,
    string? ClientSubscriptionId,
    string DesiredState,
    JsonElement? Config);

internal sealed record SubscriptionDesiredStateRequest(string DesiredState);

internal sealed record AnalysisJobCreateRequest(
    long TenantId,
    long ProviderId,
    long PipelineId,
    long? SourceId,
    string? IdempotencyKey,
    string MediaType,
    string? MediaUri,
    JsonElement? Options);

internal sealed record MediaAnalysisEventEnvelope(
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

internal sealed record MediaAnalysisWebhookBatch(IReadOnlyList<MediaAnalysisEventEnvelope> Events);

internal sealed record MediaAnalysisWebhookResult(int Accepted, int Duplicate, int Rejected);

internal sealed record ProviderSubmission(string ExternalId, string State, JsonElement? Result = null);

internal sealed record ProviderObservedState(
    string ExternalId,
    string State,
    decimal? Progress,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Result);

internal sealed record NormalizedAnalysisEvent(
    long InboxId,
    long TenantId,
    long? SourceId,
    long? SubscriptionId,
    string EventType,
    DateTimeOffset EventTime,
    string? TrackId,
    string? EntityId,
    string? EmbeddingId,
    decimal? Confidence,
    bool LateEvent,
    string DetailJson);

internal sealed record InboxQuery(string? Status, int Limit = 100);

internal sealed record ReplayRequest(
    IReadOnlyList<long>? InboxIds,
    IReadOnlyList<string>? EventIds,
    string? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit = 100,
    long? TenantId = null);
