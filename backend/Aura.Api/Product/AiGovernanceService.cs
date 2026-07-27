using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class AiGovernanceService(PgSqlConnectionFactory connectionFactory)
{
    public async Task<ProductCommandResult> CompleteEvaluationAsync(
        long evaluationRunId,
        AiEvaluationCompleteRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (request.Metrics.ValueKind != JsonValueKind.Object)
            return new(ProductCommandStatus.Invalid, Message: "metrics must be a JSON object");
        if ((request.Items?.Count ?? 0) > 10_000)
            return new(ProductCommandStatus.Invalid, Message: "At most 10000 evaluation items can be persisted per request");

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var run = await connection.QuerySingleOrDefaultAsync<EvaluationRunRow>(new CommandDefinition(
            """
            SELECT r.evaluation_run_id AS EvaluationRunId,r.tenant_id AS TenantId,r.model_release_id AS ModelReleaseId,
              r.dataset_version_id AS DatasetVersionId,r.status AS Status,m.model_code AS ModelCode,m.model_version AS ModelVersion
            FROM ai_evaluation_run r JOIN ai_model_release m ON m.model_release_id=r.model_release_id
            WHERE r.evaluation_run_id=@EvaluationRunId FOR UPDATE
            """, new { EvaluationRunId = evaluationRunId }, transaction, cancellationToken: cancellationToken));
        if (run is null) return new(ProductCommandStatus.NotFound, Message: "Evaluation run not found");
        if (request.TenantId.HasValue && run.TenantId != request.TenantId)
            return new(ProductCommandStatus.Forbidden, Message: "Evaluation run is outside the requested tenant");
        if (run.Status is "passed" or "failed" or "cancelled")
            return new(ProductCommandStatus.Conflict, Message: "Evaluation run is already terminal");

        var thresholdJson = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT threshold_json::text FROM ai_threshold_policy
            WHERE model_release_id=@ModelReleaseId AND status='active'
              AND (@TenantId IS NULL OR tenant_id=@TenantId)
            ORDER BY version DESC LIMIT 1
            """, new { run.ModelReleaseId, run.TenantId }, transaction, cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(thresholdJson))
            return new(ProductCommandStatus.Invalid, Message: "An active threshold policy is required before evaluation can complete");

        using var thresholdDocument = JsonDocument.Parse(thresholdJson);
        var checks = EvaluateThresholds(request.Metrics, thresholdDocument.RootElement);
        var passed = checks.All(check => check.Passed);
        foreach (var item in request.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.QueryRef))
                return new(ProductCommandStatus.Invalid, Message: "Every evaluation item requires queryRef");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ai_evaluation_item(evaluation_run_id,query_ref,expected_json,actual_json,metrics_json,error_category)
                VALUES(@RunId,@QueryRef,@Expected::jsonb,@Actual::jsonb,@Metrics::jsonb,@ErrorCategory)
                """, new
                {
                    RunId = evaluationRunId, QueryRef = item.QueryRef.Trim()[..Math.Min(256, item.QueryRef.Trim().Length)],
                    Expected = item.Expected.GetRawText(), Actual = item.Actual.GetRawText(), Metrics = item.Metrics.GetRawText(),
                    ErrorCategory = Clean(item.ErrorCategory, 64)
                }, transaction, cancellationToken: cancellationToken));
        }
        var environment = request.Environment?.GetRawText() ?? "{}";
        var conclusion = passed ? "thresholds_passed" : "thresholds_failed";
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ai_evaluation_run SET status=@Status,metrics_json=@Metrics::jsonb,
              environment_json=environment_json||@Environment::jsonb,artifact_uri=@Artifact,conclusion=@Conclusion,
              started_at=COALESCE(started_at,CURRENT_TIMESTAMP),completed_at=CURRENT_TIMESTAMP
            WHERE evaluation_run_id=@RunId
            """, new
            {
                RunId = evaluationRunId, Status = passed ? "passed" : "failed", Metrics = request.Metrics.GetRawText(),
                Environment = environment, Artifact = Clean(request.ArtifactUri, 2000), Conclusion = conclusion
            }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO integration_outbox(tenant_id,aggregate_type,aggregate_id,event_type,payload_json)
            VALUES(@TenantId,'ai_evaluation_run',@RunId::text,'ai.evaluation.completed',jsonb_build_object(
              'evaluationRunId',@RunId,'modelReleaseId',@ModelReleaseId,'status',@Status,'checks',@Checks::jsonb,'actor',@Actor))
            """, new
            {
                run.TenantId, RunId = evaluationRunId, run.ModelReleaseId, Status = passed ? "passed" : "failed",
                Checks = JsonSerializer.Serialize(checks), Actor = actor
            }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { evaluationRunId, status = passed ? "passed" : "failed", checks, itemCount = request.Items?.Count ?? 0 });
    }

    public async Task<ProductCommandResult> ActivateThresholdAsync(long thresholdPolicyId, long tenantId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var selected = await connection.QuerySingleOrDefaultAsync<ThresholdIdentity>(new CommandDefinition(
            "SELECT threshold_policy_id AS ThresholdPolicyId,scene_code AS SceneCode,model_release_id AS ModelReleaseId FROM ai_threshold_policy WHERE tenant_id=@TenantId AND threshold_policy_id=@Id FOR UPDATE",
            new { TenantId = tenantId, Id = thresholdPolicyId }, transaction, cancellationToken: cancellationToken));
        if (selected is null) return new(ProductCommandStatus.NotFound, Message: "Threshold policy not found");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ai_threshold_policy SET status='retired'
            WHERE tenant_id=@TenantId AND scene_code=@SceneCode AND model_release_id=@ModelReleaseId AND status='active';
            UPDATE ai_threshold_policy SET status='active',approved_by=@Actor WHERE threshold_policy_id=@Id;
            """, new { TenantId = tenantId, selected.SceneCode, selected.ModelReleaseId, Id = thresholdPolicyId, Actor = actor }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { thresholdPolicyId, status = "active" });
    }

    public async Task<ProductCommandResult> CalculateDriftAsync(AiDriftCalculateRequest request, CancellationToken cancellationToken)
    {
        if (request.WindowEnd <= request.WindowStart || request.WindowEnd - request.WindowStart > TimeSpan.FromDays(90))
            return new(ProductCommandStatus.Invalid, Message: "Drift window must be positive and no longer than 90 days");
        await using var connection = connectionFactory.CreateConnection();
        var model = await connection.QuerySingleOrDefaultAsync<ModelIdentity>(new CommandDefinition(
            "SELECT model_code AS ModelCode,model_version AS ModelVersion FROM ai_model_release WHERE model_release_id=@Id AND (tenant_id=@TenantId OR tenant_id IS NULL)",
            new { Id = request.ModelReleaseId, request.TenantId }, cancellationToken: cancellationToken));
        if (model is null) return new(ProductCommandStatus.NotFound, Message: "Model release not found");
        var counts = await connection.QuerySingleAsync<DriftCounts>(new CommandDefinition(
            """
            SELECT COUNT(*)::int AS EventCount,
              COUNT(*) FILTER(WHERE status='dismissed')::int AS DismissedEventCount,
              COALESCE(AVG(occurrence_count),0)::double precision AS AverageOccurrence
            FROM business_event WHERE tenant_id=@TenantId AND model_code=@ModelCode AND model_version=@ModelVersion
              AND last_occurred_at>=@Start AND last_occurred_at<@End
            """, new { request.TenantId, model.ModelCode, model.ModelVersion, Start = request.WindowStart, End = request.WindowEnd }, cancellationToken: cancellationToken));
        var feedback = await connection.QuerySingleAsync<FeedbackCounts>(new CommandDefinition(
            """
            SELECT COUNT(*)::int AS FeedbackCount,
              COUNT(*) FILTER(WHERE feedback_type IN ('false_positive','identity_error','relationship_error'))::int AS NegativeCount
            FROM ai_human_feedback WHERE tenant_id=@TenantId AND model_release_id=@ModelReleaseId
              AND review_status='accepted' AND created_at>=@Start AND created_at<@End
            """, new { request.TenantId, request.ModelReleaseId, Start = request.WindowStart, End = request.WindowEnd }, cancellationToken: cancellationToken));
        var prior = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT metrics_json::text FROM ai_drift_snapshot WHERE tenant_id=@TenantId AND model_release_id=@ModelReleaseId
              AND window_end<=@Start ORDER BY window_end DESC LIMIT 1
            """, new { request.TenantId, request.ModelReleaseId, Start = request.WindowStart }, cancellationToken: cancellationToken));
        var negativeRate = feedback.FeedbackCount == 0 ? 0d : feedback.NegativeCount / (double)feedback.FeedbackCount;
        var dismissalRate = counts.EventCount == 0 ? 0d : counts.DismissedEventCount / (double)counts.EventCount;
        var priorNegativeRate = ReadNumber(prior, "negativeFeedbackRate");
        var delta = priorNegativeRate.HasValue ? negativeRate - priorNegativeRate.Value : 0d;
        var status = negativeRate >= 0.35 || delta >= 0.2 ? "critical" : negativeRate >= 0.2 || delta >= 0.1 ? "warning" : "normal";
        var metrics = JsonSerializer.Serialize(new
        {
            counts.EventCount, counts.DismissedEventCount, counts.AverageOccurrence,
            feedback.FeedbackCount, feedback.NegativeCount,
            negativeFeedbackRate = negativeRate, dismissalRate, negativeFeedbackRateDelta = delta,
            model.ModelCode, model.ModelVersion
        });
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO ai_drift_snapshot(tenant_id,model_release_id,window_start,window_end,metrics_json,status)
            VALUES(@TenantId,@Model,@Start,@End,@Metrics::jsonb,@Status)
            ON CONFLICT(tenant_id,model_release_id,window_start,window_end) DO UPDATE
              SET metrics_json=EXCLUDED.metrics_json,status=EXCLUDED.status,created_at=CURRENT_TIMESTAMP
            RETURNING drift_snapshot_id
            """, new { request.TenantId, Model = request.ModelReleaseId, Start = request.WindowStart, End = request.WindowEnd, Metrics = metrics, Status = status }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { driftSnapshotId = id, status, metrics = ParseJson(metrics) });
    }

    public async Task<object> GetDashboardAsync(long tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var models = await connection.QueryAsync<dynamic>(new CommandDefinition(
            """
            SELECT m.model_release_id AS ModelReleaseId,m.model_code AS ModelCode,m.model_version AS ModelVersion,m.status AS Status,
              COUNT(DISTINCT e.evaluation_run_id) AS EvaluationCount,
              COUNT(DISTINCT e.evaluation_run_id) FILTER(WHERE e.status='passed') AS PassedCount,
              COUNT(DISTINCT f.feedback_id) AS FeedbackCount,
              COUNT(DISTINCT f.feedback_id) FILTER(WHERE f.feedback_type IN ('false_positive','identity_error','relationship_error')) AS NegativeFeedbackCount,
              (SELECT d.status FROM ai_drift_snapshot d WHERE d.tenant_id=@TenantId AND d.model_release_id=m.model_release_id ORDER BY d.window_end DESC LIMIT 1) AS DriftStatus
            FROM ai_model_release m
            LEFT JOIN ai_evaluation_run e ON e.model_release_id=m.model_release_id AND e.created_at>=@From AND e.created_at<@To
            LEFT JOIN ai_human_feedback f ON f.model_release_id=m.model_release_id AND f.tenant_id=@TenantId AND f.created_at>=@From AND f.created_at<@To
            WHERE m.tenant_id=@TenantId OR m.tenant_id IS NULL
            GROUP BY m.model_release_id,m.model_code,m.model_version,m.status ORDER BY m.model_code,m.model_version DESC
            """, new { TenantId = tenantId, From = from, To = to }, cancellationToken: cancellationToken));
        return new { tenantId, from, to, models };
    }

    private static IReadOnlyList<ThresholdCheck> EvaluateThresholds(JsonElement metrics, JsonElement thresholds)
    {
        var checks = new List<ThresholdCheck>();
        AddMinimum(checks, metrics, thresholds, "recall_at_k", "minRecallAtK");
        AddMinimum(checks, metrics, thresholds, "precision_at_k", "minPrecisionAtK");
        AddMinimum(checks, metrics, thresholds, "mrr", "minMrr");
        AddMaximum(checks, metrics, thresholds, "p95_latency_ms", "maxP95LatencyMs");
        AddMaximum(checks, metrics, thresholds, "false_positive_rate", "maxFalsePositiveRate");
        if (checks.Count == 0) checks.Add(new("thresholdPolicy", null, null, false, "No supported threshold keys were configured"));
        return checks;
    }
    private static void AddMinimum(List<ThresholdCheck> checks, JsonElement metrics, JsonElement thresholds, string metricName, string thresholdName)
    {
        if (!TryNumber(thresholds, thresholdName, out var expected)) return;
        var found = TryNumber(metrics, metricName, out var actual);
        checks.Add(new(metricName, found ? actual : null, expected, found && actual >= expected, found ? $">={expected}" : "Metric missing"));
    }
    private static void AddMaximum(List<ThresholdCheck> checks, JsonElement metrics, JsonElement thresholds, string metricName, string thresholdName)
    {
        if (!TryNumber(thresholds, thresholdName, out var expected)) return;
        var found = TryNumber(metrics, metricName, out var actual);
        checks.Add(new(metricName, found ? actual : null, expected, found && actual <= expected, found ? $"<={expected}" : "Metric missing"));
    }
    private static bool TryNumber(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var node)
            && node.TryGetDouble(out value);
    }
    private static double? ReadNumber(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return TryNumber(document.RootElement, name, out var value) ? value : null;
    }
    private static string? Clean(string? value, int max) { if (string.IsNullOrWhiteSpace(value)) return null; var clean = value.Trim(); return clean[..Math.Min(max, clean.Length)]; }
    private static JsonElement ParseJson(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    private sealed record EvaluationRunRow(long EvaluationRunId,long? TenantId,long ModelReleaseId,long DatasetVersionId,string Status,string ModelCode,string ModelVersion);
    private sealed record ThresholdIdentity(long ThresholdPolicyId,string SceneCode,long ModelReleaseId);
    private sealed record ModelIdentity(string ModelCode,string ModelVersion);
    private sealed record DriftCounts(int EventCount,int DismissedEventCount,double AverageOccurrence);
    private sealed record FeedbackCounts(int FeedbackCount,int NegativeCount);
    internal sealed record ThresholdCheck(string Metric,double? Actual,double? Threshold,bool Passed,string Requirement);
}
