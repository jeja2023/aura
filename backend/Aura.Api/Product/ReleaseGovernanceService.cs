using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class ReleaseGovernanceService(PgSqlConnectionFactory connectionFactory)
{
    internal static readonly string[] RequiredChecks =
    [
        "dotnet_tests", "python_tests", "frontend_lint", "postgres_migrations",
        "arango_real", "pgvector_target_scale", "backlog_recovery", "backup_restore",
        "upgrade_rollback", "browser_matrix", "linux_scripts", "security_privacy",
        "oidc_idp", "real_device_adapter"
    ];

    public async Task<ProductCommandResult> CreateProfileAsync(
        ServiceProfileCreateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var mode = request.DeliveryMode.Trim().ToLowerInvariant();
        if (mode is not ("private_standard" or "private_ha" or "managed_pilot"))
            return new(ProductCommandStatus.Invalid, Message: "deliveryMode is invalid");
        var validation = ValidateProfile(mode, request.Profile);
        if (validation.Count > 0) return new(ProductCommandStatus.Invalid, validation, "Service profile does not meet the commercial baseline");
        var code = RequiredCode(request.ProfileCode);
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO service_profile(profile_code,version,delivery_mode,status,profile_json,created_by)
            SELECT @Code,COALESCE(MAX(version),0)+1,@Mode,'draft',@Profile::jsonb,@Actor
            FROM service_profile WHERE profile_code=@Code RETURNING profile_id
            """, new { Code = code, Mode = mode, Profile = request.Profile.GetRawText(), Actor = actor }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { profileId = id, profileCode = code, deliveryMode = mode, status = "draft" });
    }

    public async Task<ProductPage<ServiceProfileRow>> ListProfilesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<ServiceProfileRow>(new CommandDefinition(
            $"{ProfileColumns} ORDER BY created_at DESC,profile_id DESC OFFSET @Offset LIMIT @Limit",
            new { Offset = (page - 1) * pageSize, Limit = pageSize }, cancellationToken: cancellationToken))).AsList();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM service_profile", cancellationToken: cancellationToken));
        return new(rows, page, pageSize, total);
    }

    public async Task<ServiceProfileRow?> GetProfileAsync(long profileId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<ServiceProfileRow>(new CommandDefinition(
            $"{ProfileColumns} WHERE profile_id=@Id", new { Id = profileId }, cancellationToken: cancellationToken));
    }

    public async Task<ProductCommandResult> ApproveProfileAsync(
        long profileId,
        ServiceProfileApproveRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var profile = await connection.QuerySingleOrDefaultAsync<ServiceProfileRow>(new CommandDefinition(
            $"{ProfileColumns} WHERE profile_id=@Id FOR UPDATE", new { Id = profileId }, transaction, cancellationToken: cancellationToken));
        if (profile is null) return new(ProductCommandStatus.NotFound, Message: "Service profile not found");
        if (profile.Version != request.ExpectedVersion)
            return new(ProductCommandStatus.Conflict, Message: "Service profile version conflict", CurrentVersion: profile.Version);
        if (profile.Status != "draft") return new(ProductCommandStatus.Invalid, Message: "Only a draft service profile can be approved");
        if (string.IsNullOrWhiteSpace(request.ApprovalReference)) return new(ProductCommandStatus.Invalid, Message: "Approval reference is required");
        using var document = JsonDocument.Parse(profile.ProfileJson);
        var validation = ValidateProfile(profile.DeliveryMode, document.RootElement);
        if (validation.Count > 0) return new(ProductCommandStatus.Invalid, validation, "Service profile no longer meets the commercial baseline");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE service_profile SET status='superseded' WHERE profile_code=@Code AND status='approved' AND profile_id<>@Id",
            new { Code = profile.ProfileCode, Id = profileId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE service_profile SET status='approved',approved_by=@Actor,approved_at=CURRENT_TIMESTAMP,
              profile_json=profile_json||jsonb_build_object('approvalReference',@Approval)
            WHERE profile_id=@Id
            """, new { Id = profileId, Actor = actor, Approval = request.ApprovalReference.Trim() }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { profileId, status = "approved", approvedBy = actor });
    }

    public async Task<ProductCommandResult> StartGateAsync(
        ReleaseGateStartRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var approved = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM service_profile WHERE profile_id=@Id AND status='approved')",
            new { Id = request.ServiceProfileId }, cancellationToken: cancellationToken));
        if (!approved) return new(ProductCommandStatus.Invalid, Message: "An approved service profile is required before gate execution");
        if (string.IsNullOrWhiteSpace(request.BuildVersion) || string.IsNullOrWhiteSpace(request.GitCommit) || string.IsNullOrWhiteSpace(request.MigrationVersion))
            return new(ProductCommandStatus.Invalid, Message: "buildVersion, gitCommit, and migrationVersion are required");
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO release_gate_run(service_profile_id,build_version,git_commit,image_digest,migration_version,
              environment_json,result_json,status,created_by)
            VALUES(@Profile,@Build,@Commit,@Digest,@Migration,@Environment::jsonb,
              jsonb_build_object('requiredChecks',@Required::jsonb),'running',@Actor) RETURNING gate_run_id
            """, new
            {
                Profile = request.ServiceProfileId,
                Build = request.BuildVersion.Trim(),
                Commit = request.GitCommit.Trim(),
                Digest = Clean(request.ImageDigest, 256),
                Migration = request.MigrationVersion.Trim(),
                Environment = request.Environment.GetRawText(),
                Required = JsonSerializer.Serialize(RequiredChecks),
                Actor = actor
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { gateRunId = id, status = "running", requiredChecks = RequiredChecks });
    }

    public async Task<ProductCommandResult> SubmitEvidenceAsync(
        long gateRunId,
        ReleaseGateEvidenceRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var code = request.CheckCode.Trim().ToLowerInvariant();
        if (!RequiredChecks.Contains(code, StringComparer.Ordinal))
            return new(ProductCommandStatus.Invalid, Message: "checkCode is not part of the commercial gate");
        var status = request.Status.Trim().ToLowerInvariant();
        if (status is not ("passed" or "failed" or "blocked" or "not_run"))
            return new(ProductCommandStatus.Invalid, Message: "Evidence status is invalid");
        if (status == "passed" && (request.ExitCode != 0 || string.IsNullOrWhiteSpace(request.ArtifactUri)))
            return new(ProductCommandStatus.Invalid, Message: "Passed evidence requires exitCode=0 and an artifactUri");
        await using var connection = connectionFactory.CreateConnection();
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM release_gate_run WHERE gate_run_id=@Id AND status='running')",
            new { Id = gateRunId }, cancellationToken: cancellationToken));
        if (!exists) return new(ProductCommandStatus.NotFound, Message: "Running release gate not found");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO release_gate_evidence(gate_run_id,check_code,status,command_line,exit_code,environment_json,
              metrics_json,log_summary,artifact_uri,submitted_by)
            VALUES(@RunId,@Code,@Status,@Command,@ExitCode,@Environment::jsonb,@Metrics::jsonb,@Summary,@Artifact,@Actor)
            ON CONFLICT(gate_run_id,check_code) DO UPDATE SET status=EXCLUDED.status,command_line=EXCLUDED.command_line,
              exit_code=EXCLUDED.exit_code,environment_json=EXCLUDED.environment_json,metrics_json=EXCLUDED.metrics_json,
              log_summary=EXCLUDED.log_summary,artifact_uri=EXCLUDED.artifact_uri,submitted_by=EXCLUDED.submitted_by,
              submitted_at=CURRENT_TIMESTAMP
            """, new
            {
                RunId = gateRunId,
                Code = code,
                Status = status,
                Command = Clean(request.CommandLine, 4000),
                request.ExitCode,
                Environment = request.Environment?.GetRawText() ?? "{}",
                Metrics = request.Metrics?.GetRawText() ?? "{}",
                Summary = Clean(request.LogSummary, 8000),
                Artifact = Clean(request.ArtifactUri, 4000),
                Actor = actor
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { gateRunId, checkCode = code, status });
    }

    public async Task<ProductCommandResult> CompleteGateAsync(
        long gateRunId,
        ReleaseGateCompleteRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<ReleaseGateRunRow>(new CommandDefinition(
            $"{RunColumns} WHERE r.gate_run_id=@Id FOR UPDATE OF r", new { Id = gateRunId }, transaction, cancellationToken: cancellationToken));
        if (run is null) return new(ProductCommandStatus.NotFound, Message: "Release gate not found");
        if (run.Version != request.ExpectedVersion)
            return new(ProductCommandStatus.Conflict, Message: "Release gate version conflict", CurrentVersion: run.Version);
        if (run.Status != "running") return new(ProductCommandStatus.Invalid, Message: "Only a running gate can be completed");
        var evidence = (await connection.QueryAsync<GateEvidenceRow>(new CommandDefinition(
            "SELECT check_code AS CheckCode,status AS Status,artifact_uri AS ArtifactUri,metrics_json::text AS MetricsJson FROM release_gate_evidence WHERE gate_run_id=@Id",
            new { Id = gateRunId }, transaction, cancellationToken: cancellationToken))).AsList();
        var evidenceByCode = evidence.ToDictionary(item => item.CheckCode, StringComparer.Ordinal);
        var missing = RequiredChecks.Where(code => !evidenceByCode.ContainsKey(code)).ToArray();
        var unsuccessful = evidence.Where(item => item.Status != "passed").Select(item => new { item.CheckCode, item.Status }).ToArray();
        using var environment = JsonDocument.Parse(run.EnvironmentJson);
        var realDependencies = ReadBool(environment.RootElement, "realDependencies");
        var targetHardware = ReadBool(environment.RootElement, "targetHardware");
        var secretScanClean = ReadBool(environment.RootElement, "secretScanClean");
        var passed = missing.Length == 0 && unsuccessful.Length == 0 && realDependencies && targetHardware && secretScanClean;
        var exceptionValid = ValidateException(request.Exception, out var exceptionReason);
        var status = passed ? "passed" : exceptionValid ? "exception" : "blocked";
        var result = JsonSerializer.Serialize(new
        {
            status,
            requiredChecks = RequiredChecks,
            missingChecks = missing,
            unsuccessfulChecks = unsuccessful,
            environmentAssertions = new { realDependencies, targetHardware, secretScanClean },
            exceptionReason,
            completedBy = actor,
            completedAt = DateTimeOffset.UtcNow
        });
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            UPDATE release_gate_run SET status=@Status,result_json=@Result::jsonb,exception_json=@Exception::jsonb,
              report_uri=@Report,completed_at=CURRENT_TIMESTAMP,version=version+1
            WHERE gate_run_id=@Id RETURNING version
            """, new
            {
                Id = gateRunId,
                Status = status,
                Result = result,
                Exception = request.Exception?.GetRawText(),
                Report = Clean(request.ReportUri, 4000)
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { gateRunId, status, version, missingChecks = missing, unsuccessfulChecks = unsuccessful, exceptionReason });
    }

    public async Task<object?> GetGateAsync(long gateRunId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var run = await connection.QuerySingleOrDefaultAsync<ReleaseGateRunRow>(new CommandDefinition(
            $"{RunColumns} WHERE r.gate_run_id=@Id", new { Id = gateRunId }, cancellationToken: cancellationToken));
        if (run is null) return null;
        var evidence = (await connection.QueryAsync<GateEvidenceDetailRow>(new CommandDefinition(
            """
            SELECT evidence_id AS EvidenceId,check_code AS CheckCode,status AS Status,command_line AS CommandLine,
              exit_code AS ExitCode,environment_json::text AS EnvironmentJson,metrics_json::text AS MetricsJson,
              log_summary AS LogSummary,artifact_uri AS ArtifactUri,submitted_by AS SubmittedBy,submitted_at AS SubmittedAt
            FROM release_gate_evidence WHERE gate_run_id=@Id ORDER BY check_code
            """, new { Id = gateRunId }, cancellationToken: cancellationToken))).AsList();
        return new { run, evidence, requiredChecks = RequiredChecks };
    }

    public async Task<IReadOnlyList<ProductCapabilityRow>> ListCapabilitiesAsync(string? productVersion, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<ProductCapabilityRow>(new CommandDefinition(
            """
            SELECT capability_id AS CapabilityId,capability_code AS CapabilityCode,product_version AS ProductVersion,
              status AS Status,supported_targets_json::text AS SupportedTargetsJson,limitations_json::text AS LimitationsJson,
              evidence_uri AS EvidenceUri,owner AS Owner,updated_at AS UpdatedAt
            FROM product_capability WHERE @Version IS NULL OR product_version=@Version
            ORDER BY capability_code,product_version DESC
            """, new { Version = Clean(productVersion, 32) }, cancellationToken: cancellationToken))).AsList();
    }

    private static IReadOnlyList<string> ValidateProfile(string mode, JsonElement profile)
    {
        var errors = new List<string>();
        if (profile.ValueKind != JsonValueKind.Object) return ["profile must be an object"];
        var availability = Number(profile, "coreApiAvailability");
        var rpo = Number(profile, "rpoMinutes");
        var rto = Number(profile, "rtoMinutes");
        var graphRebuild = Number(profile, "graphRebuildMinutes");
        var recall = Number(profile, "vectorRecallAt10");
        var vectorP95 = Number(profile, "vectorP95Ms");
        var graphP95 = Number(profile, "graphP95Ms");
        var backlog = Number(profile, "backlogRecoveryMinutes");
        var minAvailability = mode == "private_standard" ? 0.995m : 0.999m;
        var maxRpo = mode == "private_standard" ? 15m : 5m;
        var maxRto = mode == "private_standard" ? 240m : 60m;
        if (availability is null || availability < minAvailability) errors.Add($"coreApiAvailability must be >= {minAvailability}");
        if (rpo is null || rpo > maxRpo) errors.Add($"rpoMinutes must be <= {maxRpo}");
        if (rto is null || rto > maxRto) errors.Add($"rtoMinutes must be <= {maxRto}");
        if (graphRebuild is null || graphRebuild > 120) errors.Add("graphRebuildMinutes must be <= 120");
        if (recall is null || recall < 0.95m) errors.Add("vectorRecallAt10 must be >= 0.95");
        if (vectorP95 is null || vectorP95 > 500) errors.Add("vectorP95Ms must be <= 500");
        if (graphP95 is null || graphP95 > 1000) errors.Add("graphP95Ms must be <= 1000");
        if (backlog is null || backlog > 30) errors.Add("backlogRecoveryMinutes must be <= 30");
        if (!profile.TryGetProperty("supportMatrix", out var matrix) || matrix.ValueKind != JsonValueKind.Object)
            errors.Add("supportMatrix is required");
        else
        {
            foreach (var key in new[] { "operatingSystems", "browsers", "postgres", "arangodb", "redis", "objectStorage", "containerRuntime", "devices", "providers", "retention" })
                if (!matrix.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                    errors.Add($"supportMatrix.{key} must contain at least one approved value");
        }
        return errors;
    }

    private static bool ValidateException(JsonElement? value, out string? reason)
    {
        reason = null;
        if (!value.HasValue || value.Value.ValueKind != JsonValueKind.Object) return false;
        var node = value.Value;
        var required = new[] { "impact", "mitigation", "owner", "approver", "closeBy" };
        if (required.Any(key => !node.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            reason = "Exception is missing impact, mitigation, owner, approver, or closeBy";
            return false;
        }
        if (!DateTimeOffset.TryParse(node.GetProperty("closeBy").GetString(), out var closeBy)
            || closeBy <= DateTimeOffset.UtcNow || closeBy > DateTimeOffset.UtcNow.AddDays(30))
        {
            reason = "Exception closeBy must be in the next 30 days";
            return false;
        }
        reason = "Approved time-bounded exception";
        return true;
    }

    private static decimal? Number(JsonElement element,string key) => element.TryGetProperty(key,out var value) && value.TryGetDecimal(out var result) ? result : null;
    private static bool ReadBool(JsonElement element,string key) => element.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.True;
    private static string RequiredCode(string value)
    {
        var code = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (code.Length is 0 or > 64 || code.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("profileCode is invalid");
        return code;
    }
    private static string? Clean(string? value,int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length,max)];

    private const string ProfileColumns = """
        SELECT profile_id AS ProfileId,profile_code AS ProfileCode,version AS Version,delivery_mode AS DeliveryMode,
          status AS Status,profile_json::text AS ProfileJson,approved_by AS ApprovedBy,approved_at AS ApprovedAt,
          created_by AS CreatedBy,created_at AS CreatedAt FROM service_profile
        """;
    private const string RunColumns = """
        SELECT r.gate_run_id AS GateRunId,r.service_profile_id AS ServiceProfileId,r.build_version AS BuildVersion,
          r.git_commit AS GitCommit,r.image_digest AS ImageDigest,r.migration_version AS MigrationVersion,
          r.environment_json::text AS EnvironmentJson,r.result_json::text AS ResultJson,r.status AS Status,
          r.exception_json::text AS ExceptionJson,r.report_uri AS ReportUri,r.started_at AS StartedAt,
          r.completed_at AS CompletedAt,r.created_by AS CreatedBy,r.version AS Version FROM release_gate_run r
        """;

    internal sealed record ServiceProfileRow(long ProfileId,string ProfileCode,int Version,string DeliveryMode,string Status,string ProfileJson,string? ApprovedBy,DateTimeOffset? ApprovedAt,string CreatedBy,DateTimeOffset CreatedAt);
    private sealed record ReleaseGateRunRow(long GateRunId,long ServiceProfileId,string BuildVersion,string GitCommit,string? ImageDigest,string MigrationVersion,string EnvironmentJson,string ResultJson,string Status,string? ExceptionJson,string? ReportUri,DateTimeOffset StartedAt,DateTimeOffset? CompletedAt,string CreatedBy,int Version);
    private sealed record GateEvidenceRow(string CheckCode,string Status,string? ArtifactUri,string MetricsJson);
    private sealed record GateEvidenceDetailRow(long EvidenceId,string CheckCode,string Status,string? CommandLine,int? ExitCode,string EnvironmentJson,string MetricsJson,string? LogSummary,string? ArtifactUri,string SubmittedBy,DateTimeOffset SubmittedAt);
    internal sealed record ProductCapabilityRow(long CapabilityId,string CapabilityCode,string ProductVersion,string Status,string SupportedTargetsJson,string LimitationsJson,string? EvidenceUri,string Owner,DateTimeOffset UpdatedAt);
}
