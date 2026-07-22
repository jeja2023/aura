using System.Data;
using Aura.Api.Data;
using Aura.Api.MediaAnalysis;
using Dapper;

namespace Aura.Api.Graph;

internal sealed class GraphProjectionRepository(PgSqlConnectionFactory connectionFactory, IConfiguration configuration)
{
    private const string OutboxColumns = """
        outbox_id AS OutboxId, tenant_id AS TenantId, aggregate_type AS AggregateType,
        aggregate_id AS AggregateId, event_type AS EventType, payload_json::text AS PayloadJson,
        attempt_count AS AttemptCount, created_at AS CreatedAt
        """;

    public async Task<IReadOnlyList<IntegrationOutboxRecord>> ClaimAsync(string workerId, int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<IntegrationOutboxRecord>(Command(
            $"""
            WITH due AS (
              SELECT outbox_id FROM integration_outbox
              WHERE event_type LIKE 'graph.%' AND status IN ('pending','retry_wait')
                AND available_at<=NOW() AND (lock_until IS NULL OR lock_until<NOW())
              ORDER BY outbox_id FOR UPDATE SKIP LOCKED LIMIT @Limit
            )
            UPDATE integration_outbox o SET status='processing', attempt_count=attempt_count+1,
              locked_by=@WorkerId, lock_until=NOW()+@Lease
            FROM due WHERE o.outbox_id=due.outbox_id
            RETURNING {PrefixColumns(OutboxColumns, "o")}
            """,
            new { WorkerId = workerId, Limit = Math.Clamp(limit, 1, 500), Lease = lease }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task MarkProcessedAsync(long outboxId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE integration_outbox SET status='processed', processed_at=NOW(), locked_by=NULL,
              lock_until=NULL, last_error=NULL WHERE outbox_id=@OutboxId;
            INSERT INTO graph_projection_checkpoint(projection_name,last_outbox_id,last_projected_at,status,last_error,updated_at)
            VALUES('aura-domain-graph',@OutboxId,NOW(),'idle',NULL,NOW())
            ON CONFLICT(projection_name) DO UPDATE SET
              last_outbox_id=GREATEST(COALESCE(graph_projection_checkpoint.last_outbox_id,0),EXCLUDED.last_outbox_id),
              last_projected_at=NOW(),status='idle',last_error=NULL,updated_at=NOW();
            """,
            new { OutboxId = outboxId }, cancellationToken: cancellationToken));
    }

    public async Task MarkFailureAsync(IntegrationOutboxRecord message, Exception exception, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Clamp(configuration.GetValue("Graph:Projection:MaxAttempts", 12), 1, 100);
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE integration_outbox SET
              status=CASE WHEN @Attempts>=@MaxAttempts THEN 'dead_letter' ELSE 'retry_wait' END,
              available_at=CASE WHEN @Attempts>=@MaxAttempts THEN available_at
                ELSE NOW()+(LEAST(1800,POWER(2,LEAST(@Attempts,10))) * INTERVAL '1 second') END,
              locked_by=NULL,lock_until=NULL,last_error=LEFT(@Error,2000)
            WHERE outbox_id=@OutboxId;
            UPDATE graph_projection_checkpoint SET status='degraded',last_error=LEFT(@Error,2000),updated_at=NOW()
            WHERE projection_name='aura-domain-graph';
            """,
            new { message.OutboxId, Attempts = message.AttemptCount, MaxAttempts = maxAttempts, Error = exception.Message }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<IntegrationOutboxRecord>> QueryAsync(long? tenantId, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return (await connection.QueryAsync<IntegrationOutboxRecord>(Command(
            $"""
            SELECT {OutboxColumns} FROM integration_outbox
            WHERE event_type LIKE 'graph.%' AND (@TenantId IS NULL OR tenant_id=@TenantId)
              AND (@Status IS NULL OR status=@Status)
            ORDER BY outbox_id DESC LIMIT @Limit
            """,
            new { TenantId = tenantId, Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim(), Limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<int> ReplayAsync(long? tenantId, IReadOnlyList<long>? ids, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        if (ids is { Count: > 0 })
        {
            return await connection.ExecuteAsync(Command(
                """
                UPDATE integration_outbox SET status='pending',available_at=NOW(),locked_by=NULL,lock_until=NULL,last_error=NULL
                WHERE outbox_id=ANY(@Ids) AND event_type LIKE 'graph.%'
                  AND (@TenantId IS NULL OR tenant_id=@TenantId) AND status IN ('dead_letter','retry_wait')
                """,
                new { TenantId = tenantId, Ids = ids.Distinct().Take(1000).ToArray() }, cancellationToken: cancellationToken));
        }
        return await connection.ExecuteAsync(Command(
            """
            WITH selected AS (
              SELECT outbox_id FROM integration_outbox WHERE event_type LIKE 'graph.%' AND status=@Status
                AND (@TenantId IS NULL OR tenant_id=@TenantId)
              ORDER BY outbox_id LIMIT @Limit FOR UPDATE SKIP LOCKED)
            UPDATE integration_outbox o SET status='pending',available_at=NOW(),locked_by=NULL,lock_until=NULL,last_error=NULL
            FROM selected WHERE o.outbox_id=selected.outbox_id
            """,
            new { TenantId = tenantId, Status = string.IsNullOrWhiteSpace(status) ? "dead_letter" : status.Trim(), Limit = Math.Clamp(limit, 1, 1000) }, cancellationToken: cancellationToken));
    }

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var checkpoint = await connection.QuerySingleOrDefaultAsync<dynamic>(Command(
            """
            SELECT projection_name AS ProjectionName,last_outbox_id AS LastOutboxId,last_projected_at AS LastProjectedAt,
              graph_version AS GraphVersion,status AS Status,last_error AS LastError,updated_at AS UpdatedAt
            FROM graph_projection_checkpoint WHERE projection_name='aura-domain-graph'
            """, cancellationToken: cancellationToken));
        var counts = (await connection.QueryAsync<dynamic>(Command(
            "SELECT status AS Status,COUNT(*) AS Count,MIN(created_at) AS OldestAt FROM integration_outbox WHERE event_type LIKE 'graph.%' GROUP BY status",
            cancellationToken: cancellationToken))).AsList();
        return new { checkpoint, counts };
    }

    public async Task<long> CreateRebuildAsync(string? requestedBy, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(Command(
            "INSERT INTO graph_projection_rebuild(projection_name,requested_by) VALUES('aura-domain-graph',@RequestedBy) RETURNING rebuild_id",
            new { RequestedBy = requestedBy }, cancellationToken: cancellationToken));
    }

    public async Task<long?> ClaimRebuildAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long?>(Command(
            """
            WITH due AS (SELECT rebuild_id FROM graph_projection_rebuild WHERE status='pending' ORDER BY rebuild_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE graph_projection_rebuild r SET status='running',started_at=NOW()
            FROM due WHERE r.rebuild_id=due.rebuild_id RETURNING r.rebuild_id
            """, cancellationToken: cancellationToken));
    }

    public async Task CompleteRebuildAsync(long rebuildId, long vertices, long edges, string? error, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Command(
            """
            UPDATE graph_projection_rebuild SET status=CASE WHEN @Error IS NULL THEN 'completed' ELSE 'failed' END,
              processed_vertices=@Vertices,processed_edges=@Edges,completed_at=NOW(),last_error=LEFT(@Error,2000)
            WHERE rebuild_id=@RebuildId;
            UPDATE graph_projection_checkpoint SET status=CASE WHEN @Error IS NULL THEN 'idle' ELSE 'failed' END,
              last_projected_at=CASE WHEN @Error IS NULL THEN NOW() ELSE last_projected_at END,last_error=LEFT(@Error,2000),updated_at=NOW()
            WHERE projection_name='aura-domain-graph';
            """,
            new { RebuildId = rebuildId, Vertices = vertices, Edges = edges, Error = error }, cancellationToken: cancellationToken));
        GraphMetrics.ObserveRebuild(vertices, edges, error is null);
    }

    public PgSqlConnectionFactory ConnectionFactory => connectionFactory;

    private static string PrefixColumns(string columns, string alias) =>
        string.Join(", ", columns.Split(',').Select(column => $"{alias}.{column.Trim()}"));
    private static CommandDefinition Command(string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);
}

internal sealed record GraphReplayRequest(IReadOnlyList<long>? OutboxIds, string? Status, int Limit = 100, long? TenantId = null);
