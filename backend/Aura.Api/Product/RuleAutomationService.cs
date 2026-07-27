using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class RuleAutomationService(
    PgSqlConnectionFactory connectionFactory,
    ILogger<RuleAutomationService> logger)
{
    public async Task<IReadOnlyList<object>> EvaluateEventAsync(long tenantId, long eventId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var input = await connection.QuerySingleOrDefaultAsync<RuleInputEvent>(new CommandDefinition(
            """
            SELECT event_id AS EventId,tenant_id AS TenantId,event_no AS EventNo,event_type AS EventType,title AS Title,
              severity AS Severity,status AS Status,entity_ref AS EntityRef,space_ref AS SpaceRef,
              occurrence_count AS OccurrenceCount,last_occurred_at AS OccurredAt,version AS Version
            FROM business_event WHERE tenant_id=@TenantId AND event_id=@EventId
            """, new { TenantId = tenantId, EventId = eventId }, transaction, cancellationToken: cancellationToken));
        if (input is null) return [];

        var rules = (await connection.QueryAsync<ExecutableRule>(new CommandDefinition(
            """
            SELECT r.rule_id AS RuleId,r.rule_code AS RuleCode,r.status AS Status,r.event_limit_per_hour AS EventLimitPerHour,
              v.rule_version_id AS RuleVersionId,v.version AS Version,v.condition_json::text AS ConditionJson,
              v.action_json::text AS ActionJson,v.noise_control_json::text AS NoiseControlJson,v.rollout_json::text AS RolloutJson
            FROM automation_rule r
            JOIN automation_rule_version v ON v.rule_id=r.rule_id AND v.tenant_id=r.tenant_id AND v.version=r.active_version
            WHERE r.tenant_id=@TenantId AND r.status IN ('canary','published')
            ORDER BY r.rule_id
            """, new { TenantId = tenantId }, transaction, cancellationToken: cancellationToken))).AsList();

        var results = new List<object>(rules.Count);
        foreach (var rule in rules)
        {
            results.Add(await EvaluateRuleAsync(connection, transaction, input, rule, cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<ProductPage<JsonElement>> GetExecutionsAsync(long tenantId, long? ruleId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT e.*,v.rule_id,v.version AS rule_version,r.rule_code
              FROM automation_rule_execution e
              JOIN automation_rule_version v ON v.rule_version_id=e.rule_version_id
              JOIN automation_rule r ON r.rule_id=v.rule_id
              WHERE e.tenant_id=@TenantId AND (@RuleId IS NULL OR v.rule_id=@RuleId)
              ORDER BY e.execution_id DESC OFFSET @Offset LIMIT @PageSize
            ) x
            """, new { TenantId = tenantId, RuleId = ruleId, Offset = offset, PageSize = pageSize }, cancellationToken: cancellationToken));
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM automation_rule_execution e
            JOIN automation_rule_version v ON v.rule_version_id=e.rule_version_id
            WHERE e.tenant_id=@TenantId AND (@RuleId IS NULL OR v.rule_id=@RuleId)
            """, new { TenantId = tenantId, RuleId = ruleId }, cancellationToken: cancellationToken));
        return new(rows.Select(ParseJson).ToArray(), page, pageSize, total);
    }

    public async Task<ProductCommandResult> RollbackAsync(long ruleId, RuleRollbackRequest request, string actor, CancellationToken cancellationToken)
    {
        if (request.TargetVersion < 1 || string.IsNullOrWhiteSpace(request.Reason))
            return new(ProductCommandStatus.Invalid, Message: "targetVersion and reason are required");
        await using var connection = connectionFactory.CreateConnection();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE automation_rule r SET active_version=@Version,status='published',tripped_at=NULL,trip_reason=NULL,updated_at=CURRENT_TIMESTAMP
            WHERE r.tenant_id=@TenantId AND r.rule_id=@RuleId
              AND EXISTS(SELECT 1 FROM automation_rule_version v WHERE v.rule_id=r.rule_id AND v.tenant_id=r.tenant_id AND v.version=@Version)
            """, new { request.TenantId, RuleId = ruleId, Version = request.TargetVersion }, cancellationToken: cancellationToken));
        if (changed == 0) return new(ProductCommandStatus.NotFound, Message: "Rule or target version not found");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO integration_outbox(tenant_id,aggregate_type,aggregate_id,event_type,payload_json)
            VALUES(@TenantId,'automation_rule',@RuleId::text,'rule.rolled_back',jsonb_build_object(
              'ruleId',@RuleId,'targetVersion',@Version,'reason',@Reason,'actor',@Actor))
            """, new { request.TenantId, RuleId = ruleId, Version = request.TargetVersion, Reason = request.Reason.Trim(), Actor = actor }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { ruleId, activeVersion = request.TargetVersion, status = "published" });
    }

    internal async Task<int> ProcessPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var lastId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT last_event_id FROM automation_rule_cursor WHERE cursor_name='business_event'",
            cancellationToken: cancellationToken));
        var events = (await connection.QueryAsync<PendingEvent>(new CommandDefinition(
            """
            SELECT event_id AS EventId,tenant_id AS TenantId FROM business_event
            WHERE event_id>@LastId ORDER BY event_id LIMIT @BatchSize
            """, new { LastId = lastId, BatchSize = Math.Clamp(batchSize, 1, 1000) }, cancellationToken: cancellationToken))).AsList();
        foreach (var item in events)
        {
            await EvaluateEventAsync(item.TenantId, item.EventId, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE automation_rule_cursor SET last_event_id=GREATEST(last_event_id,@EventId),updated_at=CURRENT_TIMESTAMP WHERE cursor_name='business_event'",
                new { item.EventId }, cancellationToken: cancellationToken));
        }
        return events.Count;
    }

    private async Task<object> EvaluateRuleAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        RuleInputEvent input,
        ExecutableRule rule,
        CancellationToken cancellationToken)
    {
        using var conditionDocument = JsonDocument.Parse(rule.ConditionJson);
        using var actionDocument = JsonDocument.Parse(rule.ActionJson);
        using var noiseDocument = JsonDocument.Parse(rule.NoiseControlJson);
        using var rolloutDocument = JsonDocument.Parse(rule.RolloutJson);
        var condition = conditionDocument.RootElement;
        var noise = noiseDocument.RootElement;
        var rollout = rolloutDocument.RootElement;
        var matched = Matches(condition, input, out var factors);
        var included = IsIncluded(rollout, input, rule.RuleVersionId);
        matched &= included;
        var matchKey = MatchKey(noise, input);
        var mode = Bool(rollout, "shadow", false) ? "shadow" : rule.Status == "canary" ? "canary" : "active";
        var idempotencyKey = $"event:{input.EventId}:version:{input.Version}";

        var existing = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT execution_id FROM automation_rule_execution WHERE tenant_id=@TenantId AND rule_version_id=@RuleVersionId AND idempotency_key=@Key",
            new { input.TenantId, rule.RuleVersionId, Key = idempotencyKey }, transaction, cancellationToken: cancellationToken));
        if (existing.HasValue) return new { rule.RuleId, rule.RuleCode, executionId = existing.Value, replayed = true };

        var suppressed = false;
        string? suppressionReason = null;
        if (matched)
        {
            var hourly = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM automation_rule_execution WHERE tenant_id=@TenantId AND rule_version_id=@RuleVersionId AND matched=TRUE AND created_at>=CURRENT_TIMESTAMP-INTERVAL '1 hour'",
                new { input.TenantId, rule.RuleVersionId }, transaction, cancellationToken: cancellationToken));
            var configuredLimit = Int(noise, "maxTriggersPerHour", rule.EventLimitPerHour, 1, 1_000_000);
            if (hourly >= configuredLimit)
            {
                suppressed = true;
                suppressionReason = "hourly_limit";
                await TripRuleAsync(connection, transaction, input.TenantId, rule, configuredLimit, cancellationToken);
            }
            var suppressionMinutes = Int(noise, "suppressionMinutes", 0, 0, 10080);
            if (!suppressed && suppressionMinutes > 0)
            {
                suppressed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM automation_rule_execution
                      WHERE tenant_id=@TenantId AND rule_version_id=@RuleVersionId AND matched=TRUE AND match_key=@MatchKey
                        AND created_at>=CURRENT_TIMESTAMP-(@Minutes*INTERVAL '1 minute'))
                    """, new { input.TenantId, rule.RuleVersionId, MatchKey = matchKey, Minutes = suppressionMinutes }, transaction, cancellationToken: cancellationToken));
                if (suppressed) suppressionReason = "suppression_window";
            }
        }

        var explanation = JsonSerializer.Serialize(new
        {
            ruleCode = rule.RuleCode,
            ruleVersion = rule.Version,
            input = new { input.EventId, input.EventNo, input.EventType, input.Severity, input.Status, input.EntityRef, input.SpaceRef, input.OccurrenceCount, input.OccurredAt },
            factors,
            rolloutIncluded = included,
            matchKey,
            suppressionReason
        });
        var actionStatus = !matched ? "not_applicable" : suppressed ? "suppressed" : mode == "shadow" ? "shadowed" : "completed";
        var executionId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO automation_rule_execution(tenant_id,rule_version_id,mode,input_ref,idempotency_key,matched,
              explanation_json,action_result_json,match_key,action_status)
            VALUES(@TenantId,@RuleVersionId,@Mode,@InputRef,@Key,@Matched,@Explanation::jsonb,'{}'::jsonb,@MatchKey,@ActionStatus)
            RETURNING execution_id
            """, new
            {
                input.TenantId, rule.RuleVersionId, Mode = mode, InputRef = $"business_event:{input.EventId}", Key = idempotencyKey,
                Matched = matched, Explanation = explanation, MatchKey = matchKey, ActionStatus = actionStatus
            }, transaction, cancellationToken: cancellationToken));

        object actionResult = new { emitted = false };
        if (matched && !suppressed && mode != "shadow")
        {
            const string actionSavepoint = "rule_action";
            await transaction.SaveAsync(actionSavepoint, cancellationToken);
            try
            {
                actionResult = await ApplyActionsAsync(connection, transaction, input, rule, executionId, actionDocument.RootElement, explanation, cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE automation_rule_execution SET action_result_json=@Result::jsonb WHERE execution_id=@ExecutionId",
                    new { Result = JsonSerializer.Serialize(actionResult), ExecutionId = executionId }, transaction, cancellationToken: cancellationToken));
                await transaction.ReleaseAsync(actionSavepoint, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Rule action failed. ruleId={RuleId} eventId={EventId}", rule.RuleId, input.EventId);
                await transaction.RollbackAsync(actionSavepoint, cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE automation_rule_execution SET action_status='failed',error_message=@Error WHERE execution_id=@ExecutionId",
                    new { Error = ex.Message, ExecutionId = executionId }, transaction, cancellationToken: cancellationToken));
                actionStatus = "failed";
            }
        }
        return new { rule.RuleId, rule.RuleCode, rule.Version, executionId, matched, mode, actionStatus, actionResult };
    }

    private static async Task<object> ApplyActionsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        RuleInputEvent input,
        ExecutableRule rule,
        long executionId,
        JsonElement action,
        string explanation,
        CancellationToken cancellationToken)
    {
        long? caseId = null;
        var createCase = Bool(action, "createCase", false) || Bool(action, "humanReview", false);
        if (createCase)
        {
            var caseNo = $"CASE-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..22].ToUpperInvariant();
            caseId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO incident_case(tenant_id,case_no,title,description,status,priority,tags_json,created_at,updated_at)
                VALUES(@TenantId,@CaseNo,@Title,@Description,'new',@Priority,jsonb_build_array('rule-generated'),CURRENT_TIMESTAMP,CURRENT_TIMESTAMP)
                RETURNING case_id
                """, new
                {
                    input.TenantId, CaseNo = caseNo, Title = String(action, "caseTitle") ?? $"Rule {rule.RuleCode}: {input.Title}",
                    Description = $"Created by rule {rule.RuleCode} v{rule.Version} from event {input.EventNo}.",
                    Priority = NormalizePriority(String(action, "priority"), input.Severity)
                }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO incident_case_event(tenant_id,case_id,event_id,relation_type,relation_reason,linked_by)
                VALUES(@TenantId,@CaseId,@EventId,'primary',@Reason,@Actor)
                ON CONFLICT(case_id,event_id,relation_type) DO NOTHING;
                UPDATE business_event SET status='linked',rule_code=@RuleCode,rule_version=@Version,version=version+1,updated_at=CURRENT_TIMESTAMP
                WHERE tenant_id=@TenantId AND event_id=@EventId;
                """, new { input.TenantId, CaseId = caseId, input.EventId, Reason = "automation_rule_match", Actor = $"rule:{rule.RuleCode}", rule.RuleCode, rule.Version }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE business_event SET rule_code=@RuleCode,rule_version=@Version,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND event_id=@EventId",
                new { input.TenantId, input.EventId, rule.RuleCode, rule.Version }, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO business_event_activity(tenant_id,business_event_id,activity_type,detail_json,actor_name,idempotency_key)
            VALUES(@TenantId,@EventId,'rule_matched',@Explanation::jsonb,@Actor,@Key)
            ON CONFLICT(tenant_id,business_event_id,idempotency_key) DO NOTHING;
            INSERT INTO integration_outbox(tenant_id,aggregate_type,aggregate_id,event_type,payload_json)
            VALUES(@TenantId,'automation_rule',@RuleId::text,'rule.matched',jsonb_build_object(
              'executionId',@ExecutionId,'ruleId',@RuleId,'ruleCode',@RuleCode,'ruleVersion',@Version,
              'eventId',@EventId,'caseId',@CaseId,'action',@Action::jsonb));
            """, new
            {
                input.TenantId, input.EventId, Explanation = explanation, Actor = $"rule:{rule.RuleCode}", Key = $"rule-exec:{executionId}",
                rule.RuleId, ExecutionId = executionId, rule.RuleCode, rule.Version, CaseId = caseId, Action = action.GetRawText()
            }, transaction, cancellationToken: cancellationToken));
        return new { emitted = true, caseId, outboxEvent = "rule.matched" };
    }

    private static async Task TripRuleAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long tenantId,
        ExecutableRule rule,
        int limit,
        CancellationToken cancellationToken)
    {
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE automation_rule SET status='paused',tripped_at=CURRENT_TIMESTAMP,
              trip_reason=@Reason,updated_at=CURRENT_TIMESTAMP
            WHERE tenant_id=@TenantId AND rule_id=@RuleId AND status IN ('canary','published')
            """, new { TenantId = tenantId, rule.RuleId, Reason = $"Exceeded {limit} matches per hour" }, transaction, cancellationToken: cancellationToken));
        if (changed > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO integration_outbox(tenant_id,aggregate_type,aggregate_id,event_type,payload_json)
                VALUES(@TenantId,'automation_rule',@RuleId::text,'rule.tripped',jsonb_build_object(
                  'ruleId',@RuleId,'ruleCode',@RuleCode,'limitPerHour',@Limit,'status','paused'))
                """, new { TenantId = tenantId, rule.RuleId, rule.RuleCode, Limit = limit }, transaction, cancellationToken: cancellationToken));
        }
    }

    private static bool Matches(JsonElement condition, RuleInputEvent input, out IReadOnlyList<object> factors)
    {
        var checks = new List<object>();
        var matched = CheckString(condition, "eventType", input.EventType, checks)
            && CheckString(condition, "severity", input.Severity, checks)
            && CheckString(condition, "status", input.Status, checks)
            && CheckString(condition, "entityRef", input.EntityRef, checks)
            && CheckString(condition, "spaceRef", input.SpaceRef, checks);
        var minimum = Int(condition, "occurrenceMin", 1, 1, int.MaxValue);
        checks.Add(new { field = "occurrenceCount", expected = $">={minimum}", actual = input.OccurrenceCount, passed = input.OccurrenceCount >= minimum });
        matched &= input.OccurrenceCount >= minimum;
        var title = String(condition, "titleContains");
        if (!string.IsNullOrWhiteSpace(title))
        {
            var passed = input.Title.Contains(title, StringComparison.OrdinalIgnoreCase);
            checks.Add(new { field = "title", expected = $"contains:{title}", actual = input.Title, passed });
            matched &= passed;
        }
        var startHour = Int(condition, "startHour", 0, 0, 23);
        var endHour = Int(condition, "endHour", 23, 0, 23);
        var hour = input.OccurredAt.Hour;
        var inWindow = startHour <= endHour ? hour >= startHour && hour <= endHour : hour >= startHour || hour <= endHour;
        checks.Add(new { field = "hour", expected = $"{startHour}-{endHour}", actual = hour, passed = inWindow });
        matched &= inWindow;
        factors = checks;
        return matched;
    }

    private static bool CheckString(JsonElement root, string name, string? actual, List<object> factors)
    {
        var expected = String(root, name);
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        factors.Add(new { field = name, expected, actual, passed });
        return passed;
    }

    private static bool IsIncluded(JsonElement rollout, RuleInputEvent input, long ruleVersionId)
    {
        if (rollout.ValueKind != JsonValueKind.Object) return true;
        if (rollout.TryGetProperty("spaceRefs", out var spaces) && spaces.ValueKind == JsonValueKind.Array
            && !spaces.EnumerateArray().Any(item => string.Equals(item.GetString(), input.SpaceRef, StringComparison.OrdinalIgnoreCase))) return false;
        var percentage = Int(rollout, "percentage", 100, 0, 100);
        if (percentage == 100) return true;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{ruleVersionId}:{input.EventId}"));
        return BitConverter.ToUInt32(bytes, 0) % 100 < percentage;
    }

    private static string MatchKey(JsonElement noise, RuleInputEvent input) => (String(noise, "keyBy") ?? "event") switch
    {
        "entity" => input.EntityRef ?? $"event:{input.EventId}",
        "space" => input.SpaceRef ?? $"event:{input.EventId}",
        "eventType" => input.EventType,
        _ => $"event:{input.EventId}"
    };

    private static string NormalizePriority(string? configured, string severity) => configured?.ToLowerInvariant() switch
    {
        "low" or "normal" or "high" or "urgent" => configured.ToLowerInvariant(),
        _ => severity switch { "critical" => "urgent", "high" => "high", "low" => "low", _ => "normal" }
    };
    private static string? String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString()?.Trim() : null;
    private static bool Bool(JsonElement root, string name, bool fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var node) && node.ValueKind is JsonValueKind.True or JsonValueKind.False ? node.GetBoolean() : fallback;
    private static int Int(JsonElement root, string name, int fallback, int min, int max) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? Math.Clamp(value, min, max) : fallback;
    private static JsonElement ParseJson(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }

    private sealed record PendingEvent(long EventId,long TenantId);
    private sealed record RuleInputEvent(long EventId,long TenantId,string EventNo,string EventType,string Title,string Severity,string Status,string? EntityRef,string? SpaceRef,int OccurrenceCount,DateTimeOffset OccurredAt,int Version);
    private sealed record ExecutableRule(long RuleId,string RuleCode,string Status,int EventLimitPerHour,long RuleVersionId,int Version,string ConditionJson,string ActionJson,string NoiseControlJson,string RolloutJson);
}

internal sealed class RuleAutomationWorker(
    RuleAutomationService service,
    IConfiguration configuration,
    ILogger<RuleAutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CommercialProduct:Rules:WorkerEnabled", true)) return;
        var delay = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("CommercialProduct:Rules:PollSeconds", 3), 1, 60));
        var batchSize = Math.Clamp(configuration.GetValue("CommercialProduct:Rules:BatchSize", 100), 1, 1000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await service.ProcessPendingAsync(batchSize, stoppingToken);
                if (count == 0) await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rule automation worker iteration failed");
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}
