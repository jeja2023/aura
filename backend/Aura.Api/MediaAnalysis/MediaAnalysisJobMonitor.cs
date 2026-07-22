using System.Data;
using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.MediaAnalysis;

internal sealed class MediaAnalysisJobMonitorRepository(PgSqlConnectionFactory connectionFactory)
{
    private const string Columns = """
        job_id AS JobId, tenant_id AS TenantId, provider_id AS ProviderId, pipeline_id AS PipelineId,
        source_id AS SourceId, idempotency_key AS IdempotencyKey, external_job_id AS ExternalJobId,
        media_type AS MediaType, media_uri AS MediaUri, request_json::text AS RequestJson,
        result_json::text AS ResultJson, status AS Status, progress AS Progress, retry_count AS RetryCount,
        next_retry_at AS NextRetryAt, error_code AS ErrorCode, error_message AS ErrorMessage,
        submitted_at AS SubmittedAt, started_at AS StartedAt, completed_at AS CompletedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public async Task<MediaAnalysisJobRecord?> ClaimAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<MediaAnalysisJobRecord>(Command(
            $"""
            WITH due AS (
              SELECT job_id FROM media_analysis_job
              WHERE status IN ('accepted','running','cancelling') AND external_job_id IS NOT NULL
                AND (next_retry_at IS NULL OR next_retry_at<=NOW())
              ORDER BY COALESCE(next_retry_at,submitted_at),job_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE media_analysis_job j SET next_retry_at=NOW()+@Lease,updated_at=NOW()
            FROM due WHERE j.job_id=due.job_id RETURNING {PrefixColumns(Columns,"j")}
            """,
            new { Lease = lease }, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(long jobId, ProviderObservedState observed, CancellationToken cancellationToken)
    {
        var state = MediaAnalysisRepository.NormalizeJobState(observed.State);
        var result = observed.Result?.GetRawText() ?? "{}";
        var terminal = state is "completed" or "failed" or "cancelled" or "rejected";
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_job SET status=@Status,progress=COALESCE(@Progress,progress),
              result_json=CASE WHEN @HasResult THEN CAST(@Result AS jsonb) ELSE result_json END,
              started_at=CASE WHEN @Status='running' THEN COALESCE(started_at,NOW()) ELSE started_at END,
              completed_at=CASE WHEN @Terminal THEN COALESCE(completed_at,NOW()) ELSE completed_at END,
              next_retry_at=CASE WHEN @Terminal THEN NULL ELSE NOW()+INTERVAL '5 seconds' END,
              retry_count=0,error_code=@ErrorCode,error_message=LEFT(@ErrorMessage,2000),updated_at=NOW()
            WHERE job_id=@JobId
            """,
            new
            {
                JobId = jobId,
                Status = state,
                observed.Progress,
                HasResult = observed.Result.HasValue,
                Result = result,
                Terminal = terminal,
                observed.ErrorCode,
                observed.ErrorMessage
            }, cancellationToken: cancellationToken));
    }

    public async Task MarkPollFailureAsync(long jobId, string error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE media_analysis_job SET retry_count=retry_count+1,
              status=CASE WHEN retry_count>=9 THEN 'failed' ELSE status END,
              next_retry_at=CASE WHEN retry_count>=9 THEN NULL ELSE NOW()+(LEAST(300,POWER(2,LEAST(retry_count,8))) * INTERVAL '1 second') END,
              error_code='poll_error',error_message=LEFT(@Error,2000),updated_at=NOW()
            WHERE job_id=@JobId
            """, new { JobId = jobId, Error = error }, cancellationToken: cancellationToken));
    }

    private static string PrefixColumns(string columns, string alias) =>
        string.Join(", ", columns.Split(',').Select(column => $"{alias}.{column.Trim()}"));
    private static CommandDefinition Command(string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);
}

internal sealed class MediaAnalysisJobMonitorHostedService(
    MediaAnalysisJobMonitorRepository repository,
    MediaAnalysisRepository controlRepository,
    IMediaAnalysisProviderResolver providerResolver,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<MediaAnalysisJobMonitorHostedService> logger) : BackgroundService
{
    private const string WorkerName = "media-analysis-job-monitor";
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(250, configuration.GetValue("MediaAnalysis:Workers:JobMonitorPollMilliseconds", 1000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await repository.ClaimAsync(TimeSpan.FromMinutes(1), stoppingToken);
                if (job is null)
                {
                    await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
                try
                {
                    var provider = await controlRepository.GetProviderAsync(job.ProviderId, stoppingToken)
                        ?? throw new InvalidOperationException("Media-analysis provider no longer exists.");
                    var observed = await providerResolver.Resolve(provider).GetJobAsync(job.ExternalJobId!, stoppingToken);
                    await repository.UpdateAsync(job.JobId, observed, stoppingToken);
                    MediaAnalysisMetrics.ObserveJobOutcome(job, MediaAnalysisRepository.NormalizeJobState(observed.State));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Media-analysis job polling failed. jobId={JobId}", job.JobId);
                    await repository.MarkPollFailureAsync(job.JobId, ex.Message, stoppingToken);
                }
                await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media-analysis job monitor iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _instanceId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
