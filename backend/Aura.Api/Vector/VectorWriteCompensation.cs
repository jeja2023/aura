using Aura.Api.Data;
using Dapper;
using Aura.Api.MediaAnalysis;

namespace Aura.Api.Vector;

internal sealed class VectorWriteCompensationRepository(
    PgSqlConnectionFactory connectionFactory,
    IConfiguration configuration)
{
    public async Task EnqueueAsync(long embeddingId, Exception exception, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO vector_write_compensation(embedding_id,status,available_at,last_error)
            VALUES(@EmbeddingId,'pending',NOW(),LEFT(@Error,2000))
            ON CONFLICT(embedding_id) DO UPDATE SET status='pending',available_at=NOW(),locked_by=NULL,
              lock_until=NULL,last_error=EXCLUDED.last_error,completed_at=NULL,updated_at=NOW()
            """,
            new { EmbeddingId = embeddingId, Error = exception.Message },
            cancellationToken: cancellationToken));
    }

    public async Task<VectorCompensationRecord?> ClaimAsync(
        string workerId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<VectorCompensationRecord>(new CommandDefinition(
            """
            WITH due AS (
              SELECT compensation_id FROM vector_write_compensation
              WHERE status IN ('pending','retry_wait') AND available_at<=NOW()
                AND (lock_until IS NULL OR lock_until<NOW())
              ORDER BY compensation_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE vector_write_compensation item SET status='processing',attempt_count=attempt_count+1,
              locked_by=@WorkerId,lock_until=NOW()+@Lease,updated_at=NOW()
            FROM due WHERE item.compensation_id=due.compensation_id
            RETURNING item.compensation_id AS CompensationId,item.embedding_id AS EmbeddingId,
              item.attempt_count AS AttemptCount
            """,
            new { WorkerId = workerId, Lease = lease },
            cancellationToken: cancellationToken));
    }

    public async Task<VectorIndexDocument?> LoadDocumentAsync(long embeddingId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<VectorCompensationDocument>(new CommandDefinition(
            """
            SELECT tenant_id AS TenantId,model_id AS ModelId,vid AS Vid,capture_id AS CaptureId,
              external_embedding_id AS ExternalEmbeddingId,feature::text AS FeatureText,
              metadata_json::text AS MetadataJson
            FROM feature_embedding WHERE embedding_id=@EmbeddingId
            """,
            new { EmbeddingId = embeddingId },
            cancellationToken: cancellationToken));
        return row is null
            ? null
            : new VectorIndexDocument(row.TenantId, row.ModelId, row.Vid, row.CaptureId,
                row.ExternalEmbeddingId, VectorText.Parse(row.FeatureText), row.MetadataJson);
    }

    public async Task MarkCompletedAsync(long compensationId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE vector_write_compensation SET status='completed',completed_at=NOW(),locked_by=NULL,
              lock_until=NULL,last_error=NULL,updated_at=NOW() WHERE compensation_id=@CompensationId
            """,
            new { CompensationId = compensationId },
            cancellationToken: cancellationToken));
    }

    public async Task MarkFailureAsync(VectorCompensationRecord item, Exception exception, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(configuration.GetValue("VectorIndex:Compensation:MaxAttempts", 12), 1, 100);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE vector_write_compensation SET
              status=CASE WHEN @AttemptCount>=@MaxAttempts THEN 'dead_letter' ELSE 'retry_wait' END,
              available_at=CASE WHEN @AttemptCount>=@MaxAttempts THEN available_at
                ELSE NOW()+(LEAST(1800,POWER(2,LEAST(@AttemptCount,10))) * INTERVAL '1 second') END,
              locked_by=NULL,lock_until=NULL,last_error=LEFT(@Error,2000),updated_at=NOW()
            WHERE compensation_id=@CompensationId
            """,
            new { item.CompensationId, item.AttemptCount, MaxAttempts = maxAttempts, Error = exception.Message },
            cancellationToken: cancellationToken));
    }

    internal sealed record VectorCompensationRecord(long CompensationId, long EmbeddingId, int AttemptCount);
    private sealed record VectorCompensationDocument(
        long TenantId,
        long ModelId,
        string Vid,
        long? CaptureId,
        string? ExternalEmbeddingId,
        string FeatureText,
        string MetadataJson);
}

internal sealed class VectorWriteCompensationHostedService(
    VectorWriteCompensationRepository repository,
    LegacyArangoVectorIndex legacy,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<VectorWriteCompensationHostedService> logger) : BackgroundService
{
    private const string WorkerName = "vector-write-compensation";
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(250,
            configuration.GetValue("VectorIndex:Compensation:PollMilliseconds", 1000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = await repository.ClaimAsync(_workerId, TimeSpan.FromMinutes(2), stoppingToken);
                if (item is null)
                {
                    await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                try
                {
                    var document = await repository.LoadDocumentAsync(item.EmbeddingId, stoppingToken);
                    if (document is not null) await legacy.UpsertAsync(document, stoppingToken);
                    await repository.MarkCompletedAsync(item.CompensationId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Legacy vector compensation failed. compensationId={CompensationId}", item.CompensationId);
                    await repository.MarkFailureAsync(item, ex, stoppingToken);
                }
                await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Vector compensation worker iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _workerId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

internal static class VectorText
{
    internal static float[] Parse(string value)
    {
        var text = value.Trim();
        if (text.Length < 2 || text[0] != '[' || text[^1] != ']')
            throw new InvalidDataException("Invalid pgvector text representation.");
        var result = text[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => float.Parse(item, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        VectorValidation.Validate(result);
        return result;
    }
}
