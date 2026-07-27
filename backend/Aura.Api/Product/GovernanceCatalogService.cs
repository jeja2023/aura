using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.Internal;
using Dapper;

namespace Aura.Api.Product;

internal sealed class GovernanceCatalogService(
    PgSqlConnectionFactory connectionFactory,
    ILogger<GovernanceCatalogService> logger)
{
    private static readonly IReadOnlyDictionary<string, ResourceDescriptor> Resources =
        new Dictionary<string, ResourceDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["retention-policies"] = new("data_retention_policy", "policy_id", true, AuraPermissions.DataGovernanceView, AuraPermissions.DataGovernanceManage),
            ["processing-registers"] = new("data_processing_register", "register_id", true, AuraPermissions.DataGovernanceView, AuraPermissions.DataGovernanceManage),
            ["legal-holds"] = new("legal_hold", "legal_hold_id", true, "evidence.legal_hold", "evidence.legal_hold"),
            ["deletion-requests"] = new("data_deletion_request", "deletion_request_id", true, AuraPermissions.DataGovernanceView, AuraPermissions.DataGovernanceManage),
            ["rules"] = new("automation_rule", "rule_id", true, "rule.view", "rule.manage"),
            ["rule-versions"] = new("automation_rule_version", "rule_version_id", true, "rule.view", "rule.manage"),
            ["ai-models"] = new("ai_model_release", "model_release_id", true, "ai.governance.view", "ai.governance.manage"),
            ["ai-datasets"] = new("ai_dataset_version", "dataset_version_id", true, "ai.governance.view", "ai.governance.manage"),
            ["ai-evaluations"] = new("ai_evaluation_run", "evaluation_run_id", true, "ai.governance.view", "ai.governance.manage"),
            ["ai-feedback"] = new("ai_human_feedback", "feedback_id", true, "ai.governance.view", "ai.governance.manage"),
            ["ai-thresholds"] = new("ai_threshold_policy", "threshold_policy_id", true, "ai.governance.view", "ai.governance.manage"),
            ["ai-drift"] = new("ai_drift_snapshot", "drift_snapshot_id", true, "ai.governance.view", "ai.governance.manage"),
            ["notification-templates"] = new("notification_template", "template_id", true, "case.view", "case.manage"),
            ["notifications"] = new("notification_delivery", "notification_id", true, "case.view", "case.manage"),
            ["adapters"] = new("adapter_manifest", "adapter_id", false, "integration.view", "integration.manage"),
            ["adapter-certifications"] = new("adapter_certification", "certification_id", false, "integration.view", "integration.manage"),
            ["entitlements"] = new("tenant_entitlement", "entitlement_id", true, "usage.view", "usage.manage"),
            ["quotas"] = new("tenant_quota_policy", "quota_policy_id", true, "usage.view", "usage.manage"),
            ["usage"] = new("tenant_usage_ledger", "usage_id", true, "usage.view", "usage.manage"),
            ["costs"] = new("tenant_cost_ledger", "cost_id", true, "usage.view", "usage.manage"),
            ["slo-policies"] = new("slo_policy", "slo_policy_id", true, "ops.view", "ops.execute"),
            ["slo-snapshots"] = new("slo_snapshot", "slo_snapshot_id", false, "ops.view", "ops.execute"),
            ["maintenance-windows"] = new("maintenance_window", "maintenance_window_id", true, "ops.view", "ops.execute"),
            ["changes"] = new("platform_change_record", "change_id", true, "ops.view", "ops.execute"),
            ["metric-definitions"] = new("product_metric_definition", "metric_definition_id", false, "usage.view", "usage.manage"),
            ["metric-snapshots"] = new("product_metric_snapshot", "metric_snapshot_id", true, "usage.view", "usage.manage"),
            ["evidence-exports"] = new("evidence_export", "evidence_export_id", true, "evidence.export", "evidence.export")
        };

    public static ResourceDescriptor? Describe(string resource) => Resources.GetValueOrDefault(resource);

    public async Task<IReadOnlyList<JsonElement>> ListAsync(
        string resource,
        long? tenantId,
        int limit,
        CancellationToken cancellationToken)
    {
        var descriptor = RequireDescriptor(resource);
        limit = Math.Clamp(limit, 1, 500);
        var where = descriptor.TenantScoped ? "WHERE (@TenantId IS NULL OR t.tenant_id=@TenantId)" : string.Empty;
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT to_jsonb(t)::text FROM {descriptor.Table} t {where} ORDER BY t.{descriptor.IdColumn} DESC LIMIT @Limit",
            new { TenantId = tenantId, Limit = limit }, cancellationToken: cancellationToken));
        return rows.Select(Parse).ToArray();
    }

    public async Task<ProductCommandResult> CreateAsync(
        string resource,
        long? tenantId,
        JsonElement payload,
        string actor,
        CancellationToken cancellationToken)
    {
        _ = RequireDescriptor(resource);
        try
        {
            var id = resource.ToLowerInvariant() switch
            {
                "retention-policies" => await CreateRetentionAsync(tenantId, payload, actor, cancellationToken),
                "processing-registers" => await CreateProcessingRegisterAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "legal-holds" => await CreateLegalHoldAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "deletion-requests" => await CreateDeletionRequestAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "rules" => await CreateRuleAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "rule-versions" => await CreateRuleVersionAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "ai-models" => await CreateAiModelAsync(tenantId, payload, actor, cancellationToken),
                "ai-datasets" => await CreateAiDatasetAsync(tenantId, payload, actor, cancellationToken),
                "ai-evaluations" => await CreateAiEvaluationAsync(tenantId, payload, actor, cancellationToken),
                "ai-feedback" => await CreateAiFeedbackAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "ai-thresholds" => await CreateAiThresholdAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "ai-drift" => await CreateAiDriftAsync(RequireTenant(tenantId), payload, cancellationToken),
                "notification-templates" => await CreateNotificationTemplateAsync(tenantId, payload, actor, cancellationToken),
                "notifications" => await CreateNotificationAsync(RequireTenant(tenantId), payload, cancellationToken),
                "adapters" => await CreateAdapterAsync(payload, cancellationToken),
                "adapter-certifications" => await CreateAdapterCertificationAsync(payload, cancellationToken),
                "entitlements" => await CreateEntitlementAsync(RequireTenant(tenantId), payload, cancellationToken),
                "quotas" => await CreateQuotaAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                "usage" => await CreateUsageAsync(RequireTenant(tenantId), payload, cancellationToken),
                "costs" => await CreateCostAsync(RequireTenant(tenantId), payload, cancellationToken),
                "slo-policies" => await CreateSloPolicyAsync(tenantId, payload, cancellationToken),
                "slo-snapshots" => await CreateSloSnapshotAsync(payload, cancellationToken),
                "maintenance-windows" => await CreateMaintenanceWindowAsync(tenantId, payload, actor, cancellationToken),
                "changes" => await CreateChangeAsync(tenantId, payload, actor, cancellationToken),
                "metric-definitions" => await CreateMetricDefinitionAsync(payload, cancellationToken),
                "metric-snapshots" => await CreateMetricSnapshotAsync(tenantId, payload, cancellationToken),
                "evidence-exports" => await CreateEvidenceExportAsync(RequireTenant(tenantId), payload, actor, cancellationToken),
                _ => throw new ArgumentException("该资源不支持创建")
            };
            return ProductCommandResult.Ok(new { id, resource });
        }
        catch (ArgumentException ex)
        {
            return new(ProductCommandStatus.Invalid, Message: ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建治理资源失败。resource={Resource}, tenantId={TenantId}", resource, tenantId);
            throw;
        }
    }

    public async Task<ProductCommandResult> TransitionAsync(
        string resource,
        long id,
        long? tenantId,
        string targetStatus,
        string? reason,
        bool canApprove,
        string actor,
        CancellationToken cancellationToken)
    {
        var target = targetStatus.Trim().ToLowerInvariant();
        await using var connection = connectionFactory.CreateConnection();
        var result = resource.ToLowerInvariant() switch
        {
            "rules" => await TransitionRuleAsync(connection, id, RequireTenant(tenantId), target, canApprove, actor, cancellationToken),
            "ai-models" => await TransitionModelAsync(connection, id, tenantId, target, canApprove, actor, cancellationToken),
            "legal-holds" => await ReleaseLegalHoldAsync(connection, id, RequireTenant(tenantId), target, reason, actor, cancellationToken),
            "retention-policies" => await TransitionRetentionAsync(connection, id, tenantId, target, actor, cancellationToken),
            "ai-feedback" => await ReviewFeedbackAsync(connection, id, RequireTenant(tenantId), target, actor, cancellationToken),
            "evidence-exports" => await TransitionEvidenceExportAsync(connection, id, RequireTenant(tenantId), target, cancellationToken),
            "notification-templates" => await TransitionNotificationTemplateAsync(connection, id, tenantId, target, cancellationToken),
            _ => throw new ArgumentException("该资源不支持状态转换")
        };
        return result ? ProductCommandResult.Ok(new { id, status = target }) : new(ProductCommandStatus.NotFound, Message: "资源不存在或状态转换无效");
    }

    public async Task<object?> DryRunRuleAsync(long ruleId, RuleDryRunRequest request, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var version = await connection.QuerySingleOrDefaultAsync<RuleVersionForEvaluation>(new CommandDefinition(
            """
            SELECT v.condition_json::text AS ConditionJson,v.action_json::text AS ActionJson,r.active_version AS ActiveVersion
            FROM automation_rule_version v JOIN automation_rule r ON r.rule_id=v.rule_id AND r.tenant_id=v.tenant_id
            WHERE v.tenant_id=@TenantId AND v.rule_id=@RuleId AND v.version=@Version
            """, new { request.TenantId, RuleId = ruleId, request.Version }, cancellationToken: cancellationToken));
        if (version is null) return null;
        using var condition = JsonDocument.Parse(version.ConditionJson);
        var root = condition.RootElement;
        var eventType = OptionalString(root, "eventType");
        var severity = OptionalString(root, "severity");
        var status = OptionalString(root, "status");
        var entityRef = OptionalString(root, "entityRef");
        var spaceRef = OptionalString(root, "spaceRef");
        var occurrenceMin = OptionalInt(root, "occurrenceMin", 1);
        var limit = Math.Clamp(request.Limit, 1, 100000);
        var samples = (await connection.QueryAsync<RuleDryRunMatch>(new CommandDefinition(
            """
            SELECT event_id AS EventId,event_no AS EventNo,event_type AS EventType,severity AS Severity,status AS Status,
              occurrence_count AS OccurrenceCount,last_occurred_at AS LastOccurredAt
            FROM business_event
            WHERE tenant_id=@TenantId
              AND (@EventType IS NULL OR event_type=@EventType)
              AND (@Severity IS NULL OR severity=@Severity)
              AND (@Status IS NULL OR status=@Status)
              AND (@EntityRef IS NULL OR entity_ref=@EntityRef)
              AND (@SpaceRef IS NULL OR space_ref=@SpaceRef)
              AND occurrence_count>=@OccurrenceMin
              AND (@From IS NULL OR last_occurred_at>=@From)
              AND (@To IS NULL OR last_occurred_at<=@To)
            ORDER BY last_occurred_at DESC LIMIT @Limit
            """, new
            {
                request.TenantId,
                EventType = eventType,
                Severity = severity,
                Status = status,
                EntityRef = entityRef,
                SpaceRef = spaceRef,
                OccurrenceMin = occurrenceMin,
                request.From,
                request.To,
                Limit = limit
            }, cancellationToken: cancellationToken))).AsList();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE automation_rule_version SET dry_run_json=jsonb_build_object(
              'matchedCount',@Count,'sampleEventIds',@Ids,'evaluatedAt',CURRENT_TIMESTAMP,'from',@From,'to',@To)
            WHERE tenant_id=@TenantId AND rule_id=@RuleId AND version=@Version
            """, new { request.TenantId, RuleId = ruleId, request.Version, Count = samples.Count, Ids = samples.Take(100).Select(x => x.EventId).ToArray(), request.From, request.To }, cancellationToken: cancellationToken));
        return new
        {
            mode = "dry_run",
            deterministic = true,
            matchedCount = samples.Count,
            truncated = samples.Count == limit,
            samples = samples.Take(100),
            activeVersion = version.ActiveVersion,
            comparedVersion = request.Version,
            action = Parse(version.ActionJson)
        };
    }

    private async Task<long> CreateRetentionAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO data_retention_policy(tenant_id,data_type,online_days,archive_days,backup_days,delete_mode,version,status,updated_by)
        VALUES(@TenantId,@DataType,@OnlineDays,@ArchiveDays,@BackupDays,@DeleteMode,@Version,'draft',@Actor) RETURNING policy_id
        """, new { TenantId = tenantId, DataType = RequiredString(p,"dataType"), OnlineDays = RequiredInt(p,"onlineDays"), ArchiveDays = RequiredInt(p,"archiveDays"), BackupDays = RequiredInt(p,"backupDays"), DeleteMode = RequiredString(p,"deleteMode"), Version = OptionalInt(p,"version",1), Actor = actor }, ct);

    private async Task<long> CreateProcessingRegisterAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO data_processing_register(tenant_id,data_category,purpose,legal_basis,source,owner,regions_json,recipients_json,retention_days,deletion_method,evaluation_allowed,training_allowed,version)
        VALUES(@TenantId,@Category,@Purpose,@Basis,@Source,@Owner,@Regions::jsonb,@Recipients::jsonb,@Days,@Method,@Evaluation,@Training,@Version) RETURNING register_id
        """, new { TenantId = tenantId, Category = RequiredString(p,"dataCategory"), Purpose = RequiredString(p,"purpose"), Basis = RequiredString(p,"legalBasis"), Source = RequiredString(p,"source"), Owner = OptionalString(p,"owner") ?? actor, Regions = Raw(p,"regions","[]"), Recipients = Raw(p,"recipients","[]"), Days = RequiredInt(p,"retentionDays"), Method = RequiredString(p,"deletionMethod"), Evaluation = OptionalBool(p,"evaluationAllowed"), Training = OptionalBool(p,"trainingAllowed"), Version = OptionalInt(p,"version",1) }, ct);

    private async Task<long> CreateLegalHoldAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        WITH created AS (
          INSERT INTO legal_hold(tenant_id,hold_no,object_type,object_id,reason,reviewed_by)
          VALUES(@TenantId,@No,@Type,@ObjectId,@Reason,@Actor) RETURNING legal_hold_id),
        marked AS (
          UPDATE incident_case_evidence e SET legal_hold=TRUE
          WHERE e.tenant_id=@TenantId AND ((@Type='case' AND e.case_id::text=@ObjectId)
            OR (@Type='evidence' AND e.evidence_id::text=@ObjectId)))
        SELECT legal_hold_id FROM created
        """, new { TenantId = tenantId, No = OptionalString(p,"holdNo") ?? $"HOLD-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..22].ToUpperInvariant(), Type = RequiredString(p,"objectType"), ObjectId = RequiredString(p,"objectId"), Reason = RequiredString(p,"reason"), Actor = actor }, ct);

    private async Task<long> CreateDeletionRequestAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO data_deletion_request(tenant_id,request_no,subject_type,subject_ref,reason,scope_json,status,hold_block_json,requested_by)
        VALUES(@TenantId,@No,@Type,@Ref,@Reason,@Scope::jsonb,
          CASE WHEN EXISTS(SELECT 1 FROM legal_hold WHERE tenant_id=@TenantId AND object_type=@Type AND object_id=@Ref AND status='active') THEN 'blocked' ELSE 'reviewing' END,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('holdNo',hold_no,'reason',reason)) FROM legal_hold WHERE tenant_id=@TenantId AND object_type=@Type AND object_id=@Ref AND status='active'),'[]'::jsonb),@Actor)
        RETURNING deletion_request_id
        """, new { TenantId = tenantId, No = OptionalString(p,"requestNo") ?? $"DEL-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant(), Type = RequiredString(p,"subjectType"), Ref = RequiredString(p,"subjectRef"), Reason = RequiredString(p,"reason"), Scope = Raw(p,"scope","{}"), Actor = actor }, ct);

    private async Task<long> CreateRuleAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        "INSERT INTO automation_rule(tenant_id,rule_code,name,created_by) VALUES(@TenantId,@Code,@Name,@Actor) RETURNING rule_id",
        new { TenantId = tenantId, Code = RequiredString(p,"ruleCode"), Name = RequiredString(p,"name"), Actor = actor }, ct);

    private async Task<long> CreateRuleVersionAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO automation_rule_version(tenant_id,rule_id,version,condition_json,action_json,noise_control_json,rollout_json,created_by)
        SELECT @TenantId,@RuleId,COALESCE(MAX(v.version),0)+1,@Condition::jsonb,@Action::jsonb,@Noise::jsonb,@Rollout::jsonb,@Actor
        FROM automation_rule r
        LEFT JOIN automation_rule_version v ON v.rule_id=r.rule_id AND v.tenant_id=r.tenant_id
        WHERE r.rule_id=@RuleId AND r.tenant_id=@TenantId
        GROUP BY r.rule_id
        RETURNING rule_version_id
        """, new { TenantId = tenantId, RuleId = RequiredLong(p,"ruleId"), Condition = Raw(p,"condition","{}"), Action = Raw(p,"action","{}"), Noise = Raw(p,"noiseControl","{}"), Rollout = Raw(p,"rollout","{}"), Actor = actor }, ct);

    private async Task<long> CreateAiModelAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO ai_model_release(tenant_id,provider,model_code,model_version,purpose,vector_dimension,runtime,owner,rollout_json,rollback_json)
        VALUES(@TenantId,@Provider,@Code,@Version,@Purpose,@Dimension,@Runtime,@Owner,@Rollout::jsonb,@Rollback::jsonb) RETURNING model_release_id
        """, new { TenantId = tenantId, Provider = RequiredString(p,"provider"), Code = RequiredString(p,"modelCode"), Version = RequiredString(p,"modelVersion"), Purpose = RequiredString(p,"purpose"), Dimension = OptionalNullableInt(p,"vectorDimension"), Runtime = RequiredString(p,"runtime"), Owner = OptionalString(p,"owner") ?? actor, Rollout = Raw(p,"rollout","{}"), Rollback = Raw(p,"rollback","{}") }, ct);

    private async Task<long> CreateAiDatasetAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO ai_dataset_version(tenant_id,dataset_code,version,source_json,authorization_json,sampling_json,annotation_quality_json,sensitivity,created_by)
        VALUES(@TenantId,@Code,@Version,@Source::jsonb,@Authorization::jsonb,@Sampling::jsonb,@Quality::jsonb,@Sensitivity,@Actor) RETURNING dataset_version_id
        """, new { TenantId = tenantId, Code = RequiredString(p,"datasetCode"), Version = RequiredString(p,"version"), Source = Raw(p,"source","{}"), Authorization = Raw(p,"authorization","{}"), Sampling = Raw(p,"sampling","{}"), Quality = Raw(p,"annotationQuality","{}"), Sensitivity = RequiredString(p,"sensitivity"), Actor = actor }, ct);

    private async Task<long> CreateAiEvaluationAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO ai_evaluation_run(tenant_id,model_release_id,dataset_version_id,status,parameters_json,metrics_json,environment_json,artifact_uri,conclusion,created_by,started_at,completed_at)
        VALUES(@TenantId,@Model,@Dataset,@Status,@Parameters::jsonb,@Metrics::jsonb,@Environment::jsonb,@Artifact,@Conclusion,@Actor,CURRENT_TIMESTAMP,
          CASE WHEN @Status IN ('passed','failed') THEN CURRENT_TIMESTAMP ELSE NULL END) RETURNING evaluation_run_id
        """, new { TenantId = tenantId, Model = RequiredLong(p,"modelReleaseId"), Dataset = RequiredLong(p,"datasetVersionId"), Status = OptionalString(p,"status") ?? "queued", Parameters = Raw(p,"parameters","{}"), Metrics = Raw(p,"metrics","{}"), Environment = Raw(p,"environment","{}"), Artifact = OptionalString(p,"artifactUri"), Conclusion = OptionalString(p,"conclusion"), Actor = actor }, ct);

    private async Task<long> CreateAiFeedbackAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO ai_human_feedback(tenant_id,object_type,object_id,model_release_id,feedback_type,reason_code,note,usage_scope,submitted_by)
        VALUES(@TenantId,@ObjectType,@ObjectId,@Model,@Feedback,@Reason,@Note,'tenant_evaluation',@Actor) RETURNING feedback_id
        """, new { TenantId = tenantId, ObjectType = RequiredString(p,"objectType"), ObjectId = RequiredString(p,"objectId"), Model = OptionalNullableLong(p,"modelReleaseId"), Feedback = RequiredString(p,"feedbackType"), Reason = RequiredString(p,"reasonCode"), Note = OptionalString(p,"note"), Actor = actor }, ct);

    private async Task<long> CreateAiThresholdAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO ai_threshold_policy(tenant_id,scene_code,model_release_id,version,threshold_json,status)
        VALUES(@TenantId,@Scene,@Model,@Version,@Threshold::jsonb,'draft') RETURNING threshold_policy_id
        """, new { TenantId = tenantId, Scene = RequiredString(p,"sceneCode"), Model = RequiredLong(p,"modelReleaseId"), Version = OptionalInt(p,"version",1), Threshold = Raw(p,"threshold","{}"), Actor = actor }, ct);

    private async Task<long> CreateAiDriftAsync(long tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO ai_drift_snapshot(tenant_id,model_release_id,window_start,window_end,metrics_json,status)
        VALUES(@TenantId,@Model,@Start,@End,@Metrics::jsonb,@Status) RETURNING drift_snapshot_id
        """, new { TenantId = tenantId, Model = RequiredLong(p,"modelReleaseId"), Start = RequiredDate(p,"windowStart"), End = RequiredDate(p,"windowEnd"), Metrics = Raw(p,"metrics","{}"), Status = OptionalString(p,"status") ?? "normal" }, ct);

    private async Task<long> CreateNotificationTemplateAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO notification_template(tenant_id,template_code,version,channel,content_template,masking_policy_json,status,created_by)
        VALUES(@TenantId,@Code,@Version,@Channel,@Content,@Masking::jsonb,'draft',@Actor) RETURNING template_id
        """, new { TenantId = tenantId, Code = RequiredString(p,"templateCode"), Version = OptionalInt(p,"version",1), Channel = RequiredString(p,"channel"), Content = RequiredString(p,"contentTemplate"), Masking = Raw(p,"maskingPolicy","{}"), Actor = actor }, ct);

    private async Task<long> CreateNotificationAsync(long tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO notification_delivery(tenant_id,case_id,event_id,channel,template_code,template_version,recipient_ref,idempotency_key,status,detail_json)
        VALUES(@TenantId,@CaseId,@EventId,@Channel,@Code,@Version,@Recipient,@Key,'queued',@Detail::jsonb)
        ON CONFLICT(tenant_id,channel,idempotency_key) DO UPDATE SET idempotency_key=EXCLUDED.idempotency_key
        RETURNING notification_id
        """, new { TenantId = tenantId, CaseId = OptionalNullableLong(p,"caseId"), EventId = OptionalNullableLong(p,"eventId"), Channel = RequiredString(p,"channel"), Code = RequiredString(p,"templateCode"), Version = OptionalInt(p,"templateVersion",1), Recipient = RequiredString(p,"recipientRef"), Key = RequiredString(p,"idempotencyKey"), Detail = Raw(p,"detail","{}") }, ct);

    private async Task<long> CreateAdapterAsync(JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO adapter_manifest(adapter_code,version,protocol,lifecycle_status,manifest_json,config_schema_json,security_json,package_digest)
        VALUES(@Code,@Version,@Protocol,'experimental',@Manifest::jsonb,@Schema::jsonb,@Security::jsonb,@Digest) RETURNING adapter_id
        """, new { Code = RequiredString(p,"adapterCode"), Version = RequiredString(p,"version"), Protocol = RequiredString(p,"protocol"), Manifest = Raw(p,"manifest","{}"), Schema = Raw(p,"configSchema","{}"), Security = Raw(p,"security","{}"), Digest = OptionalString(p,"packageDigest") }, ct);

    private async Task<long> CreateAdapterCertificationAsync(JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO adapter_certification(adapter_id,device_model,firmware_version,capabilities_json,limitations_json,report_uri,status,tested_at)
        VALUES(@Adapter,@Model,@Firmware,@Capabilities::jsonb,@Limitations::jsonb,@Report,@Status,@TestedAt) RETURNING certification_id
        """, new { Adapter = RequiredLong(p,"adapterId"), Model = RequiredString(p,"deviceModel"), Firmware = RequiredString(p,"firmwareVersion"), Capabilities = Raw(p,"capabilities","{}"), Limitations = Raw(p,"limitations","[]"), Report = OptionalString(p,"reportUri"), Status = OptionalString(p,"status") ?? "experimental", TestedAt = OptionalDate(p,"testedAt") ?? DateTimeOffset.UtcNow }, ct);

    private async Task<long> CreateEntitlementAsync(long tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO tenant_entitlement(tenant_id,entitlement_code,modules_json,limits_json,support_level,valid_from,valid_to,grace_until,status,signature)
        VALUES(@TenantId,@Code,@Modules::jsonb,@Limits::jsonb,@Support,@From,@To,@Grace,'active',@Signature) RETURNING entitlement_id
        """, new { TenantId = tenantId, Code = RequiredString(p,"entitlementCode"), Modules = Raw(p,"modules","[]"), Limits = Raw(p,"limits","{}"), Support = RequiredString(p,"supportLevel"), From = RequiredDate(p,"validFrom"), To = RequiredDate(p,"validTo"), Grace = OptionalDate(p,"graceUntil"), Signature = OptionalString(p,"signature") }, ct);

    private async Task<long> CreateQuotaAsync(long tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO tenant_quota_policy(tenant_id,metric_code,limit_value,enforcement,warning_percent,valid_from,valid_to,approved_by)
        VALUES(@TenantId,@Metric,@Limit,@Enforcement,@Warning,@From,@To,@Actor) RETURNING quota_policy_id
        """, new { TenantId = tenantId, Metric = RequiredString(p,"metricCode"), Limit = RequiredDecimal(p,"limitValue"), Enforcement = RequiredString(p,"enforcement"), Warning = OptionalInt(p,"warningPercent",80), From = OptionalDate(p,"validFrom") ?? DateTimeOffset.UtcNow, To = OptionalDate(p,"validTo"), Actor = actor }, ct);

    private async Task<long> CreateUsageAsync(long tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO tenant_usage_ledger(tenant_id,project_ref,provider_ref,pipeline_ref,metric_code,quantity,unit,occurred_at,idempotency_key,adjustment_of,adjustment_reason)
        VALUES(@TenantId,@Project,@Provider,@Pipeline,@Metric,@Quantity,@Unit,@Occurred,@Key,@Adjustment,@Reason)
        ON CONFLICT(tenant_id,metric_code,idempotency_key) DO UPDATE SET idempotency_key=EXCLUDED.idempotency_key
        RETURNING usage_id
        """, new { TenantId = tenantId, Project = OptionalString(p,"projectRef"), Provider = OptionalString(p,"providerRef"), Pipeline = OptionalString(p,"pipelineRef"), Metric = RequiredString(p,"metricCode"), Quantity = RequiredDecimal(p,"quantity"), Unit = RequiredString(p,"unit"), Occurred = OptionalDate(p,"occurredAt") ?? DateTimeOffset.UtcNow, Key = RequiredString(p,"idempotencyKey"), Adjustment = OptionalNullableLong(p,"adjustmentOf"), Reason = OptionalString(p,"adjustmentReason") }, ct);

    private async Task<long> CreateCostAsync(long tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO tenant_cost_ledger(tenant_id,usage_id,cost_category,amount,currency,calculation_version,occurred_at,detail_json)
        VALUES(@TenantId,@Usage,@Category,@Amount,@Currency,@Version,@Occurred,@Detail::jsonb) RETURNING cost_id
        """, new { TenantId = tenantId, Usage = OptionalNullableLong(p,"usageId"), Category = RequiredString(p,"costCategory"), Amount = RequiredDecimal(p,"amount"), Currency = RequiredString(p,"currency"), Version = RequiredString(p,"calculationVersion"), Occurred = OptionalDate(p,"occurredAt") ?? DateTimeOffset.UtcNow, Detail = Raw(p,"detail","{}") }, ct);

    private async Task<long> CreateSloPolicyAsync(long? tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO slo_policy(service_profile_id,tenant_id,metric_code,target,comparison,window_seconds,warning_percent,tighten_percent,freeze_percent,effective_at)
        VALUES(@Profile,@TenantId,@Metric,@Target,@Comparison,@Window,@Warning,@Tighten,@Freeze,@Effective) RETURNING slo_policy_id
        """, new { Profile = RequiredLong(p,"serviceProfileId"), TenantId = tenantId, Metric = RequiredString(p,"metricCode"), Target = RequiredDecimal(p,"target"), Comparison = RequiredString(p,"comparison"), Window = RequiredLong(p,"windowSeconds"), Warning = OptionalInt(p,"warningPercent",50), Tighten = OptionalInt(p,"tightenPercent",75), Freeze = OptionalInt(p,"freezePercent",100), Effective = OptionalDate(p,"effectiveAt") ?? DateTimeOffset.UtcNow }, ct);

    private async Task<long> CreateSloSnapshotAsync(JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO slo_snapshot(slo_policy_id,window_start,window_end,numerator,denominator,value,error_budget_consumed_percent,status,dimensions_json)
        VALUES(@Policy,@Start,@End,@Numerator,@Denominator,@Value,@Budget,@Status,@Dimensions::jsonb) RETURNING slo_snapshot_id
        """, new { Policy = RequiredLong(p,"sloPolicyId"), Start = RequiredDate(p,"windowStart"), End = RequiredDate(p,"windowEnd"), Numerator = RequiredDecimal(p,"numerator"), Denominator = RequiredDecimal(p,"denominator"), Value = OptionalDecimal(p,"value"), Budget = OptionalDecimal(p,"errorBudgetConsumedPercent"), Status = RequiredString(p,"status"), Dimensions = Raw(p,"dimensions","{}") }, ct);

    private async Task<long> CreateMaintenanceWindowAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO maintenance_window(tenant_id,title,starts_at,ends_at,impact_json,approved_by)
        VALUES(@TenantId,@Title,@Start,@End,@Impact::jsonb,@Actor) RETURNING maintenance_window_id
        """, new { TenantId = tenantId, Title = RequiredString(p,"title"), Start = RequiredDate(p,"startsAt"), End = RequiredDate(p,"endsAt"), Impact = Raw(p,"impact","{}"), Actor = actor }, ct);

    private async Task<long> CreateChangeAsync(long? tenantId, JsonElement p, string actor, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO platform_change_record(tenant_id,change_type,version_ref,summary,diff_json,changed_by)
        VALUES(@TenantId,@Type,@Version,@Summary,@Diff::jsonb,@Actor) RETURNING change_id
        """, new { TenantId = tenantId, Type = RequiredString(p,"changeType"), Version = RequiredString(p,"versionRef"), Summary = RequiredString(p,"summary"), Diff = Raw(p,"diff","{}"), Actor = actor }, ct);

    private async Task<long> CreateMetricDefinitionAsync(JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO product_metric_definition(metric_code,version,name,numerator_definition,denominator_definition,window_definition,effective_at)
        VALUES(@Code,@Version,@Name,@Numerator,@Denominator,@Window,@Effective) RETURNING metric_definition_id
        """, new { Code = RequiredString(p,"metricCode"), Version = OptionalInt(p,"version",1), Name = RequiredString(p,"name"), Numerator = OptionalString(p,"numeratorDefinition"), Denominator = OptionalString(p,"denominatorDefinition"), Window = RequiredString(p,"windowDefinition"), Effective = OptionalDate(p,"effectiveAt") ?? DateTimeOffset.UtcNow }, ct);

    private async Task<long> CreateMetricSnapshotAsync(long? tenantId, JsonElement p, CancellationToken ct) => await ScalarAsync(
        """
        INSERT INTO product_metric_snapshot(tenant_id,metric_definition_id,window_start,window_end,numerator,denominator,value,dimensions_json)
        VALUES(@TenantId,@Definition,@Start,@End,@Numerator,@Denominator,@Value,@Dimensions::jsonb) RETURNING metric_snapshot_id
        """, new { TenantId = tenantId, Definition = RequiredLong(p,"metricDefinitionId"), Start = RequiredDate(p,"windowStart"), End = RequiredDate(p,"windowEnd"), Numerator = OptionalDecimal(p,"numerator"), Denominator = OptionalDecimal(p,"denominator"), Value = OptionalDecimal(p,"value"), Dimensions = Raw(p,"dimensions","{}") }, ct);

    private async Task<long> CreateEvidenceExportAsync(long tenantId, JsonElement p, string actor, CancellationToken ct)
    {
        var manifest = Raw(p,"manifest","{}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        return await ScalarAsync(
            """
            INSERT INTO evidence_export(tenant_id,case_id,purpose,masking_policy,manifest_json,manifest_sha256,artifact_key,status,requested_by,expires_at)
            VALUES(@TenantId,@CaseId,@Purpose,@Masking,@Manifest::jsonb,@Hash,@Artifact,'generating',@Actor,@Expires) RETURNING evidence_export_id
            """, new { TenantId = tenantId, CaseId = RequiredLong(p,"caseId"), Purpose = RequiredString(p,"purpose"), Masking = RequiredString(p,"maskingPolicy"), Manifest = manifest, Hash = hash, Artifact = RequiredString(p,"artifactKey"), Actor = actor, Expires = OptionalDate(p,"expiresAt") ?? DateTimeOffset.UtcNow.AddHours(24) }, ct);
    }

    private static async Task<bool> TransitionRuleAsync(System.Data.IDbConnection c,long id,long tenantId,string target,bool approve,string actor,CancellationToken ct)
    {
        var allowed = target is "draft" or "pending_approval" or "canary" or "published" or "paused" or "archived";
        if (!allowed || ((target is "canary" or "published") && !approve)) return false;
        return await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE automation_rule SET status=@Target,
              active_version=CASE WHEN @Target IN ('canary','published') THEN (SELECT MAX(version) FROM automation_rule_version WHERE rule_id=@Id) ELSE active_version END,
              updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND rule_id=@Id
              AND (@Target NOT IN ('canary','published') OR EXISTS(SELECT 1 FROM automation_rule_version WHERE rule_id=@Id AND dry_run_json<>'{}'::jsonb))
            """, new { Target = target, Id = id, TenantId = tenantId, Actor = actor }, cancellationToken: ct)) > 0;
    }

    private static async Task<bool> TransitionModelAsync(System.Data.IDbConnection c,long id,long? tenantId,string target,bool approve,string actor,CancellationToken ct)
    {
        var allowed = target is "evaluating" or "approved" or "canary" or "production" or "deprecated" or "rolled_back";
        if (!allowed || ((target is "approved" or "canary" or "production") && !approve)) return false;
        return await c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ai_model_release SET status=@Target,approved_by=CASE WHEN @Target IN ('approved','canary','production') THEN @Actor ELSE approved_by END,
              released_at=CASE WHEN @Target='production' THEN CURRENT_TIMESTAMP ELSE released_at END
            WHERE model_release_id=@Id AND (@TenantId IS NULL OR tenant_id=@TenantId)
              AND (@Target NOT IN ('approved','canary','production') OR EXISTS(SELECT 1 FROM ai_evaluation_run WHERE model_release_id=@Id AND status='passed'))
            """, new { Target = target, Actor = actor, Id = id, TenantId = tenantId }, cancellationToken: ct)) > 0;
    }

    private static async Task<bool> ReleaseLegalHoldAsync(System.Data.IDbConnection c,long id,long tenantId,string target,string? reason,string actor,CancellationToken ct)
    {
        if (target != "released" || string.IsNullOrWhiteSpace(reason)) return false;
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            WITH released AS (
              UPDATE legal_hold SET status='released',released_by=@Actor,released_reason=@Reason,released_at=CURRENT_TIMESTAMP
              WHERE tenant_id=@TenantId AND legal_hold_id=@Id AND status='active' RETURNING object_type,object_id),
            affected AS (
              SELECT e.evidence_id FROM incident_case_evidence e,released r WHERE e.tenant_id=@TenantId
                AND ((r.object_type='case' AND e.case_id::text=r.object_id) OR (r.object_type='evidence' AND e.evidence_id::text=r.object_id))),
            cleared AS (
              UPDATE incident_case_evidence e SET legal_hold=FALSE FROM affected a WHERE e.evidence_id=a.evidence_id
                AND NOT EXISTS(SELECT 1 FROM legal_hold h WHERE h.tenant_id=e.tenant_id AND h.status='active'
                  AND ((h.object_type='case' AND h.object_id=e.case_id::text) OR (h.object_type='evidence' AND h.object_id=e.evidence_id::text))))
            SELECT COUNT(*) FROM released
            """,
            new { Actor = actor, Reason = reason.Trim(), TenantId = tenantId, Id = id }, cancellationToken: ct)) > 0;
    }

    private static async Task<bool> TransitionRetentionAsync(System.Data.IDbConnection c,long id,long? tenantId,string target,string actor,CancellationToken ct)
    {
        if (target is not ("active" or "superseded")) return false;
        return await c.ExecuteAsync(new CommandDefinition(
            """
            WITH selected AS (
              SELECT policy_id,tenant_id,data_type FROM data_retention_policy
              WHERE policy_id=@Id AND (@TenantId IS NULL OR tenant_id=@TenantId)),
            superseded AS (
              UPDATE data_retention_policy p SET status='superseded',updated_by=@Actor,updated_at=CURRENT_TIMESTAMP
              FROM selected s WHERE @Target='active' AND p.data_type=s.data_type
                AND p.policy_id<>s.policy_id AND p.status='active' AND p.tenant_id IS NOT DISTINCT FROM s.tenant_id)
            UPDATE data_retention_policy p SET status=@Target,updated_by=@Actor,updated_at=CURRENT_TIMESTAMP
            FROM selected s WHERE p.policy_id=s.policy_id
            """, new { Id = id, TenantId = tenantId, Target = target, Actor = actor }, cancellationToken: ct)) > 0;
    }

    private static async Task<bool> ReviewFeedbackAsync(System.Data.IDbConnection c,long id,long tenantId,string target,string actor,CancellationToken ct)
    {
        if (target is not ("accepted" or "rejected")) return false;
        return await c.ExecuteAsync(new CommandDefinition(
            "UPDATE ai_human_feedback SET review_status=@Target,reviewed_by=@Actor,reviewed_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND feedback_id=@Id AND review_status='pending'",
            new { Target = target, Actor = actor, TenantId = tenantId, Id = id }, cancellationToken: ct)) > 0;
    }

    private static async Task<bool> TransitionEvidenceExportAsync(System.Data.IDbConnection c,long id,long tenantId,string target,CancellationToken ct)
    {
        if (target is not ("ready" or "failed" or "expired" or "revoked")) return false;
        return await c.ExecuteAsync(new CommandDefinition(
            "UPDATE evidence_export SET status=@Target,completed_at=CASE WHEN @Target IN ('ready','failed') THEN CURRENT_TIMESTAMP ELSE completed_at END WHERE tenant_id=@TenantId AND evidence_export_id=@Id",
            new { Target = target, TenantId = tenantId, Id = id }, cancellationToken: ct)) > 0;
    }

    private static async Task<bool> TransitionNotificationTemplateAsync(System.Data.IDbConnection c,long id,long? tenantId,string target,CancellationToken ct)
    {
        if (target is not ("active" or "retired")) return false;
        return await c.ExecuteAsync(new CommandDefinition(
            "UPDATE notification_template SET status=@Target WHERE template_id=@Id AND (@TenantId IS NULL OR tenant_id=@TenantId)",
            new { Target = target, Id = id, TenantId = tenantId }, cancellationToken: ct)) > 0;
    }

    private async Task<long> ScalarAsync(string sql, object args, CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql,args,cancellationToken:ct));
    }

    private static ResourceDescriptor RequireDescriptor(string resource) => Resources.TryGetValue(resource, out var descriptor)
        ? descriptor : throw new ArgumentException("未知治理资源");
    private static long RequireTenant(long? tenantId) => tenantId is > 0 ? tenantId.Value : throw new ArgumentException("该资源必须指定 tenantId");
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static string RequiredString(JsonElement p,string name) => OptionalString(p,name) ?? throw new ArgumentException($"缺少 {name}");
    private static string? OptionalString(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
    private static long RequiredLong(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.TryGetInt64(out var result) ? result : throw new ArgumentException($"缺少 {name}");
    private static long? OptionalNullableLong(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.TryGetInt64(out var result) ? result : null;
    private static int RequiredInt(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.TryGetInt32(out var result) ? result : throw new ArgumentException($"缺少 {name}");
    private static int OptionalInt(JsonElement p,string name,int fallback) => p.TryGetProperty(name,out var v) && v.TryGetInt32(out var result) ? result : fallback;
    private static int? OptionalNullableInt(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.TryGetInt32(out var result) ? result : null;
    private static bool OptionalBool(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.True;
    private static decimal RequiredDecimal(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.TryGetDecimal(out var result) ? result : throw new ArgumentException($"缺少 {name}");
    private static decimal? OptionalDecimal(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.TryGetDecimal(out var result) ? result : null;
    private static DateTimeOffset RequiredDate(JsonElement p,string name) => OptionalDate(p,name) ?? throw new ArgumentException($"缺少 {name}");
    private static DateTimeOffset? OptionalDate(JsonElement p,string name) => p.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(),out var result) ? result : null;
    private static string Raw(JsonElement p,string name,string fallback) => p.TryGetProperty(name,out var v) ? v.GetRawText() : fallback;

    internal sealed record ResourceDescriptor(string Table,string IdColumn,bool TenantScoped,string ViewPermission,string ManagePermission);
    private sealed record RuleVersionForEvaluation(string ConditionJson,string ActionJson,int? ActiveVersion);
    private sealed record RuleDryRunMatch(long EventId,string EventNo,string EventType,string Severity,string Status,int OccurrenceCount,DateTimeOffset LastOccurredAt);
}
