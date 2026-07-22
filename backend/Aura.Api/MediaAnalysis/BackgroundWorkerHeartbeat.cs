using Aura.Api.Data;
using Dapper;
using Prometheus;

namespace Aura.Api.MediaAnalysis;

internal sealed class BackgroundWorkerHeartbeat(
    PgSqlConnectionFactory connectionFactory,
    ILogger<BackgroundWorkerHeartbeat> logger)
{
    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "aura_background_worker_last_success_timestamp_seconds",
        "Unix timestamp of the last successful worker iteration.",
        new GaugeConfiguration { LabelNames = ["worker"] });
    private static readonly Gauge Healthy = Metrics.CreateGauge(
        "aura_background_worker_healthy",
        "Whether the last worker iteration succeeded.",
        new GaugeConfiguration { LabelNames = ["worker"] });

    public Task SuccessAsync(string workerName, string instanceId, CancellationToken cancellationToken) =>
        WriteAsync(workerName, instanceId, true, null, cancellationToken);

    public Task FailureAsync(string workerName, string instanceId, Exception exception, CancellationToken cancellationToken) =>
        WriteAsync(workerName, instanceId, false, exception.Message, cancellationToken);

    private async Task WriteAsync(
        string workerName,
        string instanceId,
        bool success,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO background_worker_heartbeat(worker_name,instance_id,status,last_success_at,last_error_at,last_error)
                VALUES(@WorkerName,@InstanceId,@Status,CASE WHEN @Success THEN NOW() ELSE NULL END,
                  CASE WHEN @Success THEN NULL ELSE NOW() END,LEFT(@Error,2000))
                ON CONFLICT(worker_name,instance_id) DO UPDATE SET status=EXCLUDED.status,
                  last_success_at=CASE WHEN @Success THEN NOW() ELSE background_worker_heartbeat.last_success_at END,
                  last_error_at=CASE WHEN @Success THEN background_worker_heartbeat.last_error_at ELSE NOW() END,
                  last_error=CASE WHEN @Success THEN NULL ELSE EXCLUDED.last_error END,updated_at=NOW()
                """,
                new
                {
                    WorkerName = workerName,
                    InstanceId = instanceId,
                    Status = success ? "running" : "degraded",
                    Success = success,
                    Error = error
                }, cancellationToken: cancellationToken));
            Healthy.WithLabels(workerName).Set(success ? 1 : 0);
            if (success) LastSuccess.WithLabels(workerName).SetToCurrentTimeUtc();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to persist worker heartbeat. worker={WorkerName}", workerName);
        }
    }
}
