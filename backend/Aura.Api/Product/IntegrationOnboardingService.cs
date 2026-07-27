using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.MediaAnalysis;
using Dapper;

namespace Aura.Api.Product;

internal sealed class IntegrationOnboardingService(
    PgSqlConnectionFactory connectionFactory,
    MediaAnalysisOutboundUrlPolicy outboundPolicy)
{
    public async Task<ProductPage<OnboardingRow>> ListAsync(long tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var connection = connectionFactory.CreateConnection();
        var args = new { TenantId = tenantId, Offset = (page - 1) * pageSize, PageSize = pageSize };
        var rows = (await connection.QueryAsync<OnboardingRow>(new CommandDefinition(
            $"{Columns} WHERE tenant_id=@TenantId ORDER BY updated_at DESC,onboarding_id DESC OFFSET @Offset LIMIT @PageSize",
            args, cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM integration_onboarding WHERE tenant_id=@TenantId", args, cancellationToken: cancellationToken));
        return new(rows, page, pageSize, total);
    }

    public async Task<OnboardingRow?> GetAsync(long tenantId, long onboardingId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<OnboardingRow>(new CommandDefinition(
            $"{Columns} WHERE tenant_id=@TenantId AND onboarding_id=@OnboardingId",
            new { TenantId = tenantId, OnboardingId = onboardingId }, cancellationToken: cancellationToken));
    }

    public async Task<object> CreateAsync(OnboardingCreateRequest request, string actor, CancellationToken cancellationToken)
    {
        var type = NormalizeType(request.IntegrationType);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO integration_onboarding(tenant_id,integration_type,name,created_by)
            VALUES(@TenantId,@Type,@Name,@Actor) RETURNING onboarding_id
            """, new { request.TenantId, Type = type, Name = Clean(request.Name, "未命名接入", 128), Actor = actor }, transaction, cancellationToken: cancellationToken));
        await SnapshotAsync(connection, transaction, id, request.TenantId, actor, cancellationToken);
        await AddActivityAsync(connection, transaction, id, request.TenantId, 1, "created", "{}", actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new { onboardingId = id, currentStep = 1, status = "draft", configVersion = 1 };
    }

    public async Task<ProductCommandResult> ApplyStepAsync(
        long tenantId,
        long onboardingId,
        OnboardingStepRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (request.Step is < 1 or > 7) return new(ProductCommandStatus.Invalid, Message: "向导步骤必须为 1-7");
        if (ContainsPlainSecret(request.Config))
            return new(ProductCommandStatus.Invalid, Message: "配置中不得包含明文密钥，请使用 secretReferences");
        if (request.SecretReferences.HasValue && !ValidSecretReferences(request.SecretReferences.Value))
            return new(ProductCommandStatus.Invalid, Message: "密钥引用必须使用 env://、vault:// 或 k8s://");

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<OnboardingRow>(new CommandDefinition(
            $"{Columns} WHERE tenant_id=@TenantId AND onboarding_id=@OnboardingId FOR UPDATE",
            new { TenantId = tenantId, OnboardingId = onboardingId }, transaction, cancellationToken: cancellationToken));
        if (current is null) return new(ProductCommandStatus.NotFound, Message: "接入向导不存在");
        if (request.Step > current.CurrentStep + 1)
            return new(ProductCommandStatus.Invalid, Message: "必须按顺序完成接入向导");

        var diagnostic = await ValidateStepAsync(request.Step, request.Config, request.RunTest, cancellationToken);
        if (!diagnostic.Passed && string.IsNullOrWhiteSpace(request.ExemptionReason))
            return new(ProductCommandStatus.Invalid, diagnostic, diagnostic.Message);

        var diagnosticJson = JsonSerializer.Serialize(new
        {
            step = request.Step,
            diagnostic.Passed,
            diagnostic.Message,
            diagnostic.Detail,
            exempted = !diagnostic.Passed,
            exemptionReason = request.ExemptionReason,
            checkedAt = DateTimeOffset.UtcNow
        });
        var nextStep = Math.Max(current.CurrentStep, request.Step);
        var nextStatus = request.Step switch
        {
            7 when diagnostic.Passed || !string.IsNullOrWhiteSpace(request.ExemptionReason) => "enabled",
            >= 6 when diagnostic.Passed => "ready",
            _ when request.RunTest && !diagnostic.Passed => "failed",
            _ => "testing"
        };
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE integration_onboarding SET
              current_step=@NextStep,status=@Status,
              config_json=config_json || @Config::jsonb,
              secret_refs_json=secret_refs_json || @Secrets::jsonb,
              capability_json=CASE WHEN @Step=3 THEN capability_json || @Config::jsonb ELSE capability_json END,
              diagnostics_json=jsonb_set(diagnostics_json,ARRAY['step'||@Step::text],@Diagnostic::jsonb,TRUE),
              exemption_reason=@ExemptionReason,config_version=config_version+1,updated_at=CURRENT_TIMESTAMP
            WHERE tenant_id=@TenantId AND onboarding_id=@OnboardingId RETURNING config_version
            """, new
            {
                TenantId = tenantId,
                OnboardingId = onboardingId,
                NextStep = nextStep,
                Status = nextStatus,
                Config = request.Config.GetRawText(),
                Secrets = request.SecretReferences?.GetRawText() ?? "{}",
                Step = request.Step,
                Diagnostic = diagnosticJson,
                ExemptionReason = CleanNullable(request.ExemptionReason, 2000)
            }, transaction, cancellationToken: cancellationToken));
        await SnapshotAsync(connection, transaction, onboardingId, tenantId, actor, cancellationToken);
        await AddActivityAsync(connection, transaction, onboardingId, tenantId, request.Step,
            diagnostic.Passed ? "step_passed" : "step_exempted", diagnosticJson, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { onboardingId, currentStep = nextStep, status = nextStatus, configVersion = version, diagnostic });
    }

    public async Task<ProductCommandResult> RollbackAsync(
        long tenantId,
        long onboardingId,
        int targetVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE integration_onboarding current SET
              current_step=history.current_step,status=CASE WHEN history.status='enabled' THEN 'ready' ELSE history.status END,
              config_json=history.config_json,secret_refs_json=history.secret_refs_json,capability_json=history.capability_json,
              diagnostics_json=history.diagnostics_json,config_version=current.config_version+1,updated_at=CURRENT_TIMESTAMP
            FROM integration_onboarding_version history
            WHERE current.tenant_id=@TenantId AND current.onboarding_id=@OnboardingId
              AND history.onboarding_id=current.onboarding_id AND history.tenant_id=current.tenant_id AND history.version=@TargetVersion
            """, new { TenantId = tenantId, OnboardingId = onboardingId, TargetVersion = targetVersion }, transaction, cancellationToken: cancellationToken));
        if (affected == 0) return new(ProductCommandStatus.NotFound, Message: "指定配置版本不存在");
        await SnapshotAsync(connection, transaction, onboardingId, tenantId, actor, cancellationToken);
        await AddActivityAsync(connection, transaction, onboardingId, tenantId, 1, "version_rolled_back",
            JsonSerializer.Serialize(new { targetVersion }), actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { onboardingId, targetVersion });
    }

    public async Task<object?> ExportAsync(long tenantId, long onboardingId, CancellationToken cancellationToken)
    {
        var row = await GetAsync(tenantId, onboardingId, cancellationToken);
        if (row is null) return null;
        return new
        {
            schemaVersion = "1.0",
            row.IntegrationType,
            row.Name,
            row.CurrentStep,
            row.Status,
            config = JsonSerializer.Deserialize<JsonElement>(row.ConfigJson),
            capabilities = JsonSerializer.Deserialize<JsonElement>(row.CapabilityJson),
            diagnostics = JsonSerializer.Deserialize<JsonElement>(row.DiagnosticsJson),
            row.ConfigVersion,
            containsSecrets = false
        };
    }

    private async Task<StepDiagnostic> ValidateStepAsync(int step, JsonElement config, bool runTest, CancellationToken cancellationToken)
    {
        if (!runTest) return new(true, "配置已保存，尚未执行联通测试", new { tested = false });
        switch (step)
        {
            case 1:
                return new(true, "接入类型已确认", new { });
            case 2:
            case 5:
                var property = step == 2 ? "endpoint" : "sourceUrl";
                if (!config.TryGetProperty(property, out var url) || string.IsNullOrWhiteSpace(url.GetString()))
                    return new(false, $"缺少 {property}", new { });
                var uri = await outboundPolicy.ValidateAsync(url.GetString()!, cancellationToken);
                return new(true, "地址通过 DNS、协议与出站策略检查", new { scheme = uri.Scheme, host = uri.Host });
            case 3:
                var hasVersion = config.TryGetProperty("protocolVersion", out var protocol) && !string.IsNullOrWhiteSpace(protocol.GetString());
                return new(hasVersion, hasVersion ? "能力与协议版本已登记" : "缺少 protocolVersion", new { });
            case 4:
                var model = config.TryGetProperty("modelVersion", out var modelVersion) && !string.IsNullOrWhiteSpace(modelVersion.GetString());
                var dimension = config.TryGetProperty("vectorDimension", out var vectorDimension) && vectorDimension.TryGetInt32(out var value) && value > 0;
                return new(model && dimension, model && dimension ? "流水线模型和向量维度有效" : "缺少模型版本或向量维度", new { });
            case 6:
                var schema = config.TryGetProperty("schemaVersion", out var schemaVersion) && !string.IsNullOrWhiteSpace(schemaVersion.GetString());
                var eventType = config.TryGetProperty("eventType", out var eventName) && !string.IsNullOrWhiteSpace(eventName.GetString());
                return new(schema && eventType, schema && eventType ? "样例事件契约有效" : "样例缺少 schemaVersion 或 eventType", new { });
            case 7:
                var confirmed = config.TryGetProperty("confirmEnable", out var confirm) && confirm.ValueKind == JsonValueKind.True;
                return new(confirmed, confirmed ? "接入已确认启用" : "启用前必须显式确认", new { });
            default:
                return new(true, "步骤完成", new { });
        }
    }

    private static bool ContainsPlainSecret(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if ((name.Contains("password") || name.Contains("secret") || name.Contains("token") || name.Contains("apikey") || name.Contains("api_key"))
                && property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                return true;
            if (property.Value.ValueKind == JsonValueKind.Object && ContainsPlainSecret(property.Value)) return true;
        }
        return false;
    }

    private static bool ValidSecretReferences(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        return element.EnumerateObject().All(property =>
            property.Value.ValueKind == JsonValueKind.String
            && (property.Value.GetString()!.StartsWith("env://", StringComparison.OrdinalIgnoreCase)
                || property.Value.GetString()!.StartsWith("vault://", StringComparison.OrdinalIgnoreCase)
                || property.Value.GetString()!.StartsWith("k8s://", StringComparison.OrdinalIgnoreCase)));
    }

    private static Task SnapshotAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, long id, long tenantId, string actor, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO integration_onboarding_version(
              onboarding_id,tenant_id,version,current_step,status,config_json,secret_refs_json,capability_json,diagnostics_json,created_by)
            SELECT onboarding_id,tenant_id,config_version,current_step,status,config_json,secret_refs_json,capability_json,diagnostics_json,@Actor
            FROM integration_onboarding WHERE onboarding_id=@Id AND tenant_id=@TenantId
            ON CONFLICT(onboarding_id,version) DO NOTHING
            """, new { Id = id, TenantId = tenantId, Actor = actor }, transaction, cancellationToken: ct));

    private static Task AddActivityAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, long id, long tenantId, int step, string action, string result, string actor, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO integration_onboarding_activity(onboarding_id,tenant_id,step,action,result_json,actor) VALUES(@Id,@TenantId,@Step,@Action,@Result::jsonb,@Actor)",
            new { Id = id, TenantId = tenantId, Step = step, Action = action, Result = result, Actor = actor }, transaction, cancellationToken: ct));

    private static string NormalizeType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "standard_http" => "standard_http",
        "device_vms" => "device_vms",
        "file_upload" => "file_upload",
        "object_storage" => "object_storage",
        _ => throw new ArgumentException("不支持的接入类型")
    };
    private static string Clean(string? value, string fallback, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
    private static string? CleanNullable(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : Clean(value, string.Empty, maxLength);

    private const string Columns = """
        SELECT onboarding_id AS OnboardingId,tenant_id AS TenantId,integration_type AS IntegrationType,name AS Name,
          current_step AS CurrentStep,status AS Status,config_json::text AS ConfigJson,secret_refs_json::text AS SecretRefsJson,
          capability_json::text AS CapabilityJson,diagnostics_json::text AS DiagnosticsJson,config_version AS ConfigVersion,
          exemption_reason AS ExemptionReason,created_by AS CreatedBy,updated_at AS UpdatedAt,created_at AS CreatedAt
        FROM integration_onboarding
        """;
}

internal sealed record StepDiagnostic(bool Passed,string Message,object Detail);
internal sealed record OnboardingRow(
    long OnboardingId,long TenantId,string IntegrationType,string Name,int CurrentStep,string Status,
    string ConfigJson,string SecretRefsJson,string CapabilityJson,string DiagnosticsJson,int ConfigVersion,
    string? ExemptionReason,string CreatedBy,DateTimeOffset UpdatedAt,DateTimeOffset CreatedAt);

