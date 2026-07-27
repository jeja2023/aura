using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class EntitlementUsageService(
    PgSqlConnectionFactory connectionFactory,
    IConfiguration configuration)
{
    public async Task<EntitlementDecision> CheckAsync(
        EntitlementCheckRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await CheckAsync(connection, null, request, false, cancellationToken);
    }

    public async Task<ProductCommandResult> RecordAsync(
        UsageRecordRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Quantity == 0) return new(ProductCommandStatus.Invalid, Message: "Usage quantity cannot be zero");
        if (request.Quantity < 0 && (!request.AdjustmentOf.HasValue || string.IsNullOrWhiteSpace(request.AdjustmentReason)))
            return new(ProductCommandStatus.Invalid, Message: "Negative usage requires adjustmentOf and adjustmentReason");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            return new(ProductCommandStatus.Invalid, Message: "A stable idempotencyKey of at most 128 characters is required");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var prior = await connection.QuerySingleOrDefaultAsync<UsageLedgerRow>(new CommandDefinition(
            """
            SELECT usage_id AS UsageId,quantity AS Quantity,unit AS Unit,occurred_at AS OccurredAt
            FROM tenant_usage_ledger WHERE tenant_id=@TenantId AND metric_code=@Metric AND idempotency_key=@Key
            """, new { request.TenantId, Metric = request.MetricCode, Key = request.IdempotencyKey }, transaction, cancellationToken: cancellationToken));
        if (prior is not null)
            return new(ProductCommandStatus.Duplicate, prior, "Usage was already recorded for this idempotency key");
        var decision = await CheckAsync(connection, transaction,
            new EntitlementCheckRequest(request.TenantId, request.ModuleCode, request.MetricCode, request.Quantity), true, cancellationToken);
        if (!decision.Allowed)
            return new(ProductCommandStatus.Forbidden, decision, decision.Reason);
        if (request.AdjustmentOf.HasValue)
        {
            var originalExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM tenant_usage_ledger WHERE tenant_id=@TenantId AND usage_id=@Id)",
                new { request.TenantId, Id = request.AdjustmentOf.Value }, transaction, cancellationToken: cancellationToken));
            if (!originalExists) return new(ProductCommandStatus.Invalid, Message: "Adjustment target does not exist in this tenant");
        }
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO tenant_usage_ledger(tenant_id,project_ref,provider_ref,pipeline_ref,metric_code,quantity,unit,
              occurred_at,idempotency_key,adjustment_of,adjustment_reason)
            VALUES(@TenantId,@Project,@Provider,@Pipeline,@Metric,@Quantity,@Unit,@Occurred,@Key,@Adjustment,@Reason)
            RETURNING usage_id
            """, new
            {
                request.TenantId,
                Project = Clean(request.ProjectRef, 128),
                Provider = Clean(request.ProviderRef, 128),
                Pipeline = Clean(request.PipelineRef, 128),
                Metric = Required(request.MetricCode, "metricCode", 128),
                request.Quantity,
                Unit = Required(request.Unit, "unit", 32),
                Occurred = request.OccurredAt ?? DateTimeOffset.UtcNow,
                Key = request.IdempotencyKey.Trim(),
                Adjustment = request.AdjustmentOf,
                Reason = Clean(request.AdjustmentReason, 512)
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { usageId = id, decision, recorded = true });
    }

    public async Task<object> GetReportAsync(long tenantId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);
        if (end <= start) throw new ArgumentException("to must be later than from");
        await using var connection = connectionFactory.CreateConnection();
        var usage = (await connection.QueryAsync<UsageSummaryRow>(new CommandDefinition(
            """
            SELECT metric_code AS MetricCode,unit AS Unit,SUM(quantity) AS Quantity,
              COUNT(*) AS LedgerEntries,COUNT(DISTINCT project_ref) AS Projects,
              COUNT(DISTINCT provider_ref) AS Providers,COUNT(DISTINCT pipeline_ref) AS Pipelines
            FROM tenant_usage_ledger WHERE tenant_id=@TenantId AND occurred_at>=@From AND occurred_at<@To
            GROUP BY metric_code,unit ORDER BY metric_code,unit
            """, new { TenantId = tenantId, From = start, To = end }, cancellationToken: cancellationToken))).AsList();
        var costs = (await connection.QueryAsync<CostSummaryRow>(new CommandDefinition(
            """
            SELECT cost_category AS CostCategory,currency AS Currency,SUM(amount) AS Amount,COUNT(*) AS Entries
            FROM tenant_cost_ledger WHERE tenant_id=@TenantId AND occurred_at>=@From AND occurred_at<@To
            GROUP BY cost_category,currency ORDER BY cost_category,currency
            """, new { TenantId = tenantId, From = start, To = end }, cancellationToken: cancellationToken))).AsList();
        var decisions = new List<EntitlementDecision>();
        foreach (var metric in usage)
            decisions.Add(await CheckAsync(new EntitlementCheckRequest(tenantId, "*", metric.MetricCode, 0), cancellationToken));
        return new { tenantId, from = start, to = end, usage, costs, quota = decisions, generatedAt = DateTimeOffset.UtcNow };
    }

    private async Task<EntitlementDecision> CheckAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction,
        EntitlementCheckRequest request,
        bool lockQuota,
        CancellationToken ct)
    {
        var mode = configuration["CommercialProduct:Entitlements:EnforcementMode"]?.Trim().ToLowerInvariant() ?? "report_only";
        var entitlement = await connection.QuerySingleOrDefaultAsync<EntitlementStateRow>(new CommandDefinition(
            """
            SELECT entitlement_id AS EntitlementId,entitlement_code AS EntitlementCode,modules_json::text AS ModulesJson,
              limits_json::text AS LimitsJson,support_level AS SupportLevel,valid_from AS ValidFrom,valid_to AS ValidTo,
              grace_until AS GraceUntil,status AS Status,signature AS Signature
            FROM tenant_entitlement WHERE tenant_id=@TenantId AND valid_from<=CURRENT_TIMESTAMP
            ORDER BY valid_from DESC,entitlement_id DESC LIMIT 1
            """, new { request.TenantId }, transaction, cancellationToken: ct));
        if (entitlement is null)
            return mode == "report_only"
                ? new(true, "unlicensed_report_only", "No entitlement is installed; private-deployment report-only mode allows the request", null, null, null, false)
                : new(false, "unlicensed", "No active tenant entitlement is installed", null, null, null, true);
        var now = DateTimeOffset.UtcNow;
        var effectiveStatus = entitlement.Status;
        if (effectiveStatus is not ("revoked" or "restricted"))
            effectiveStatus = now <= entitlement.ValidTo ? "active" : entitlement.GraceUntil.HasValue && now <= entitlement.GraceUntil ? "grace" : "expired";
        var signatureValid = ValidateSignature(request.TenantId, entitlement);
        if (!signatureValid && mode == "enforce")
            return new(false, effectiveStatus, "Entitlement signature is missing or invalid", entitlement.EntitlementCode, null, null, true);
        if (effectiveStatus is "revoked" or "restricted" or "expired")
            return new(false, effectiveStatus, "Entitlement is not writable; customer data remains intact", entitlement.EntitlementCode, null, null, true);
        var modules = ParseModules(entitlement.ModulesJson);
        var moduleAllowed = request.ModuleCode == "*" || modules.Contains("*") || modules.Contains(request.ModuleCode);
        if (!moduleAllowed)
            return new(false, effectiveStatus, $"Module '{request.ModuleCode}' is not entitled", entitlement.EntitlementCode, null, null, true);
        if (string.IsNullOrWhiteSpace(request.MetricCode))
            return new(true, effectiveStatus, effectiveStatus == "grace" ? "Entitlement is in its grace period" : "Entitlement permits the module", entitlement.EntitlementCode, null, null, false);

        var lockSql = lockQuota ? " FOR UPDATE OF q" : string.Empty;
        var quota = await connection.QuerySingleOrDefaultAsync<QuotaStateRow>(new CommandDefinition(
            $"""
            SELECT q.quota_policy_id AS QuotaPolicyId,q.limit_value AS LimitValue,q.enforcement AS Enforcement,
              q.warning_percent AS WarningPercent,q.valid_from AS ValidFrom,q.valid_to AS ValidTo,
              COALESCE((SELECT SUM(u.quantity) FROM tenant_usage_ledger u WHERE u.tenant_id=q.tenant_id
                AND u.metric_code=q.metric_code AND u.occurred_at>=q.valid_from AND (q.valid_to IS NULL OR u.occurred_at<q.valid_to)),0) AS Used
            FROM tenant_quota_policy q WHERE q.tenant_id=@TenantId AND q.metric_code=@Metric
              AND q.valid_from<=CURRENT_TIMESTAMP AND (q.valid_to IS NULL OR q.valid_to>CURRENT_TIMESTAMP)
            ORDER BY q.valid_from DESC,q.quota_policy_id DESC LIMIT 1{lockSql}
            """, new { request.TenantId, Metric = request.MetricCode.Trim() }, transaction, cancellationToken: ct));
        if (quota is null)
            return new(true, effectiveStatus, "No active quota policy exists for the metric", entitlement.EntitlementCode, null, null, false);
        var projected = quota.Used + request.Quantity;
        var percentage = quota.LimitValue <= 0 ? (projected > 0 ? 1000m : 0m) : projected / quota.LimitValue * 100;
        var exceeded = projected > quota.LimitValue;
        var hardDenied = exceeded && quota.Enforcement == "hard" && mode == "enforce";
        return new(!hardDenied, effectiveStatus,
            hardDenied ? "Hard quota would be exceeded" : exceeded ? "Soft quota exceeded; usage is allowed and must alert" : percentage >= quota.WarningPercent ? "Quota warning threshold reached" : "Quota available",
            entitlement.EntitlementCode, quota.LimitValue, projected, exceeded || percentage >= quota.WarningPercent);
    }

    private bool ValidateSignature(long tenantId, EntitlementStateRow entitlement)
    {
        var key = configuration["CommercialProduct:Entitlements:SignatureKey"];
        if (string.IsNullOrWhiteSpace(key)) return configuration["CommercialProduct:Entitlements:EnforcementMode"] != "enforce";
        if (string.IsNullOrWhiteSpace(entitlement.Signature)) return false;
        var canonical = $"{tenantId}|{entitlement.EntitlementCode}|{entitlement.ValidFrom:O}|{entitlement.ValidTo:O}|{entitlement.ModulesJson}|{entitlement.LimitsJson}";
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(entitlement.Signature.ToLowerInvariant()));
    }

    private static HashSet<string> ParseModules(string json)
    {
        try { return (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
    private static string Required(string? value,string name,int max) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required") : value.Trim()[..Math.Min(value.Trim().Length,max)];
    private static string? Clean(string? value,int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length,max)];

    private sealed record EntitlementStateRow(long EntitlementId,string EntitlementCode,string ModulesJson,string LimitsJson,string SupportLevel,DateTimeOffset ValidFrom,DateTimeOffset ValidTo,DateTimeOffset? GraceUntil,string Status,string? Signature);
    private sealed record QuotaStateRow(long QuotaPolicyId,decimal LimitValue,string Enforcement,int WarningPercent,DateTimeOffset ValidFrom,DateTimeOffset? ValidTo,decimal Used);
    private sealed record UsageLedgerRow(long UsageId,decimal Quantity,string Unit,DateTimeOffset OccurredAt);
    private sealed record UsageSummaryRow(string MetricCode,string Unit,decimal Quantity,long LedgerEntries,long Projects,long Providers,long Pipelines);
    private sealed record CostSummaryRow(string CostCategory,string Currency,decimal Amount,long Entries);
}

internal sealed record EntitlementDecision(
    bool Allowed,string Status,string Reason,string? EntitlementCode,decimal? LimitValue,decimal? ProjectedUsage,bool Warning);
