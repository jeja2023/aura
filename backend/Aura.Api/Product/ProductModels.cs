using System.Text.Json;

namespace Aura.Api.Product;

internal sealed record BusinessEventCreateRequest(
    long TenantId,
    string EventType,
    string Title,
    string? Summary,
    string Severity,
    string AggregationKey,
    int AggregationPolicyVersion,
    DateTimeOffset OccurredAt,
    string? RuleCode,
    int? RuleVersion,
    string? ModelCode,
    string? ModelVersion,
    string? EntityRef,
    string? SpaceRef,
    JsonElement? RepresentativeEvidence,
    long? AnalysisEventId);

internal sealed record EventTransitionRequest(
    string Action,
    int ExpectedVersion,
    string? ReasonCode,
    JsonElement? Detail);

internal sealed record EventActionRequest(int ExpectedVersion, string? ReasonCode, JsonElement? Detail);

internal sealed record EventBatchTriageRequest(
    long TenantId,
    IReadOnlyList<long> EventIds,
    long? TriageUserId,
    string? TriageUserName);

internal sealed record CaseCreateRequest(
    long TenantId,
    string Title,
    string? Description,
    string Priority,
    long? OwnerUserId,
    string? OwnerName,
    IReadOnlyList<long>? EventIds,
    IReadOnlyList<string>? Tags,
    DateTimeOffset? AcknowledgeDueAt,
    DateTimeOffset? StartDueAt,
    DateTimeOffset? ResolveDueAt);

internal sealed record CaseTransitionRequest(
    string TargetStatus,
    int ExpectedVersion,
    string? ReasonCode,
    JsonElement? Resolution);

internal sealed record CaseEventLinkRequest(
    long EventId,
    string? RelationType,
    string? Reason,
    bool Active = true);

internal sealed record CaseCommentRequest(string Content, string? Visibility);

internal sealed record CaseEvidenceRequest(
    string EvidenceType,
    string SourceType,
    string SourceId,
    string ObjectKey,
    string Sha256,
    string? MediaType,
    string? MaskingPolicy,
    string Purpose);

internal sealed record CaseParticipantRequest(long TenantId, long UserId, string RoleType);
internal sealed record CaseChecklistUpdateRequest(long TenantId, string Status, JsonElement? Detail);
internal sealed record CaseTemplateWriteRequest(
    long TenantId,
    string TemplateCode,
    int Version,
    string Name,
    string? EventType,
    string DefaultPriority,
    JsonElement? DefaultSla,
    JsonElement Checklist,
    JsonElement RequiredEvidence);
internal sealed record CaseTemplateStateRequest(long TenantId, string TargetStatus);
internal sealed record CaseMergeRequest(long TenantId, long TargetCaseId, string Reason, int ExpectedSourceVersion);
internal sealed record CaseSplitRequest(
    long TenantId,
    string Title,
    string? Description,
    string Priority,
    IReadOnlyList<long> EventIds,
    string Reason,
    int ExpectedSourceVersion);

internal sealed record InvestigationCreateRequest(long TenantId, string Title);

internal sealed record InvestigationQueryRequest(
    string QueryType,
    JsonElement Query,
    string? ModelCode,
    string? ModelVersion,
    int? ThresholdPolicyVersion,
    string? DataVersion);

internal sealed record InvestigationEvidenceRequest(
    string SourceType,
    string SourceId,
    string Sha256,
    string? Note);

internal sealed record InvestigationAttachRequest(long CaseId, IReadOnlyList<long> EvidenceItemIds);

internal sealed record HighRiskTaskPreviewRequest(
    long? TenantId,
    string OperationType,
    JsonElement Scope,
    int RequestedBatchSize,
    string? TicketNo);

internal sealed record HighRiskTaskExecuteRequest(
    string ConfirmationPhrase,
    string? TicketNo,
    bool StepUpVerified,
    int ExpectedVersion);

internal sealed record HighRiskTaskCancelRequest(int ExpectedVersion);

internal sealed record OnboardingCreateRequest(long TenantId, string IntegrationType, string Name);

internal sealed record OnboardingStepRequest(
    int Step,
    JsonElement Config,
    JsonElement? SecretReferences,
    bool RunTest,
    string? ExemptionReason);

internal sealed record OnboardingRollbackRequest(int TargetVersion);

internal sealed record GovernanceResourceCreateRequest(
    long? TenantId,
    string ResourceType,
    JsonElement Payload);

internal sealed record GovernanceWriteRequest(long? TenantId, JsonElement Payload);
internal sealed record GovernanceTransitionRequest(long? TenantId, string TargetStatus, string? Reason);
internal sealed record RuleDryRunRequest(long TenantId, int Version, DateTimeOffset? From, DateTimeOffset? To, int Limit = 10000);
internal sealed record RuleEvaluateRequest(long TenantId, long EventId);
internal sealed record RuleRollbackRequest(long TenantId, int TargetVersion, string Reason);
internal sealed record AiEvaluationCompleteRequest(
    long? TenantId, JsonElement Metrics, IReadOnlyList<AiEvaluationItemRequest>? Items,
    string? ArtifactUri, JsonElement? Environment);
internal sealed record AiEvaluationItemRequest(
    string QueryRef, JsonElement Expected, JsonElement Actual, JsonElement Metrics, string? ErrorCategory);
internal sealed record AiDriftCalculateRequest(long TenantId, long ModelReleaseId, DateTimeOffset WindowStart, DateTimeOffset WindowEnd);
internal sealed record BreakGlassRegisterRequest(long UserId, string CredentialCustodian);
internal sealed record BreakGlassStateRequest(bool Enabled, string Reason, int DurationMinutes = 30);
internal sealed record BreakGlassExerciseRequest(string Reason, bool Successful);
internal sealed record AdapterContractRunRequest(
    long AdapterId, string DeviceModel, string FirmwareVersion, JsonElement Environment,
    IReadOnlyList<AdapterContractCheckRequest> Checks, string? ReportUri);
internal sealed record AdapterContractCheckRequest(string Code, string Status, string? Detail);
internal sealed record AnalyticsEventRequest(
    long TenantId, string EventName, string? ObjectType, string? ObjectId, JsonElement? Properties, string? SessionRef);
internal sealed record MobileDraftWriteRequest(
    long TenantId, Guid ClientDraftId, string ActionType, string ObjectType, string? ObjectId,
    int? BaseVersion, JsonElement Payload, DateTimeOffset? ExpiresAt);
internal sealed record MobileDraftSyncRequest(long TenantId, int? CurrentVersion);
internal sealed record MobilePushSubscriptionRequest(long TenantId, string EndpointUri, string KeyP256dh, string KeyAuth, string? UserAgent);
internal sealed record MobileDeepLinkRequest(long TenantId, string ObjectType, string ObjectId, string? Reason);
internal sealed record ControlledQueryRequest(long TenantId, long? InvestigationId, string Text);
internal sealed record ControlledQueryPlanUpdateRequest(long TenantId, JsonElement Plan);
internal sealed record ControlledQueryConfirmRequest(long TenantId, bool Confirm);

internal sealed record CleanupJobCreateRequest(long? TenantId, long PolicyId, bool DryRun, int BatchLimit = 500);
internal sealed record CleanupJobCancelRequest(int ExpectedVersion);

internal sealed record LegacyMigrationStartRequest(long? TenantId, string BatchName, bool DryRun, DateTimeOffset ShadowDeadline);
internal sealed record LegacyMigrationBackfillRequest(int BatchSize = 500);
internal sealed record LegacyMigrationCutoverRequest(string TargetReadMode, int ExpectedVersion, string ApprovalReference);

internal sealed record OidcProviderWriteRequest(
    long TenantId,string ProviderCode,string Authority,string ClientId,string? ClientSecretRef,
    string RedirectUri,IReadOnlyList<string>? Scopes,bool RequireMfa = true);
internal sealed record OidcProviderTransitionRequest(long TenantId,bool Enabled,int ExpectedVersion);
internal sealed record IdentityGroupMappingWriteRequest(long TenantId,long OidcProviderId,string ExternalGroup,string RoleName);
internal sealed record IdentityGroupPreviewRequest(long TenantId,long OidcProviderId,IReadOnlyList<string> Groups);
internal sealed record AuthSessionRevokeRequest(string Reason);
internal sealed record StepUpChallengeRequest(string Action,string? ResourceRef,string RequiredStrength = "mfa");

internal sealed record EntitlementCheckRequest(long TenantId,string ModuleCode,string? MetricCode,decimal Quantity = 0);
internal sealed record UsageRecordRequest(
    long TenantId,string ModuleCode,string MetricCode,decimal Quantity,string Unit,string IdempotencyKey,
    string? ProjectRef,string? ProviderRef,string? PipelineRef,DateTimeOffset? OccurredAt,long? AdjustmentOf,string? AdjustmentReason);

internal sealed record ServiceProfileCreateRequest(string ProfileCode,string DeliveryMode,JsonElement Profile);
internal sealed record ServiceProfileApproveRequest(int ExpectedVersion,string ApprovalReference);
internal sealed record ReleaseGateStartRequest(
    long ServiceProfileId,string BuildVersion,string GitCommit,string? ImageDigest,string MigrationVersion,JsonElement Environment);
internal sealed record ReleaseGateEvidenceRequest(
    string CheckCode,string Status,string? CommandLine,int? ExitCode,JsonElement? Environment,
    JsonElement? Metrics,string? LogSummary,string? ArtifactUri);
internal sealed record ReleaseGateCompleteRequest(
    int ExpectedVersion,string? ReportUri,JsonElement? Exception);

internal sealed record EvidenceExportCreateRequest(
    long TenantId,string Purpose,string MaskingPolicy,IReadOnlyList<long>? EvidenceIds,int ExpiresInHours = 24);
internal sealed record EvidenceAccessGrantRequest(string Grantee,string Purpose,int ExpiresInMinutes = 60,int MaxDownloads = 1);

internal sealed record NotificationSendRequest(
    long TenantId,long? CaseId,long? EventId,string Channel,string TemplateCode,string RecipientRef,
    string IdempotencyKey,JsonElement Payload,string? FallbackChannel,string? FallbackRecipient,int MaxAttempts = 5);
internal sealed record NotificationReceiptRequest(long TenantId,string ProviderReceiptId,string Status,JsonElement Payload);
internal sealed record NotificationChannelConfigWriteRequest(
    long? TenantId,string Channel,string ProviderCode,string? EndpointUri,string? SecretRef,JsonElement? Config,int Version = 1);
internal sealed record NotificationChannelConfigStateRequest(long? TenantId,string TargetStatus);

internal sealed record DbBusinessEvent(
    long EventId,
    long TenantId,
    string EventNo,
    string EventType,
    string Title,
    string? Summary,
    string Severity,
    string Status,
    long? TriageUserId,
    string? TriageUserName,
    string? RuleCode,
    int? RuleVersion,
    string? ModelCode,
    string? ModelVersion,
    string? EntityRef,
    string? SpaceRef,
    int OccurrenceCount,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    string RepresentativeEvidenceJson,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? CaseId,
    string? CaseNo,
    string? CaseStatus);

internal sealed record DbIncidentCase(
    long CaseId,
    long TenantId,
    string CaseNo,
    string Title,
    string? Description,
    string Status,
    string? StatusReason,
    string Priority,
    long? OwnerUserId,
    string? OwnerName,
    string TagsJson,
    string? ExternalTicketNo,
    DateTimeOffset? AcknowledgeDueAt,
    DateTimeOffset? StartDueAt,
    DateTimeOffset? ResolveDueAt,
    DateTimeOffset? PausedAt,
    long AccumulatedPauseSeconds,
    int EscalationLevel,
    string? ResolutionJson,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    int EventCount,
    int EvidenceCount);

internal sealed record ProductPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

internal enum ProductCommandStatus
{
    Success,
    NotFound,
    Conflict,
    Invalid,
    Duplicate,
    Forbidden
}

internal sealed record ProductCommandResult(
    ProductCommandStatus Status,
    object? Data = null,
    string? Message = null,
    int? CurrentVersion = null)
{
    public static ProductCommandResult Ok(object? data = null, string? message = null) =>
        new(ProductCommandStatus.Success, data, message);
}
