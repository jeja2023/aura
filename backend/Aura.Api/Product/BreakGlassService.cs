using System.Text.Json;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Product;

internal sealed class BreakGlassService(PgSqlConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<JsonElement>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT to_jsonb(x)::text FROM (
              SELECT b.break_glass_account_id,b.user_id,u.user_name,u.display_name,b.credential_custodian,b.enabled,
                b.enabled_until,b.last_exercised_at,b.last_rotated_at,b.created_at,
                (SELECT MAX(e.occurred_at) FROM break_glass_event e WHERE e.break_glass_account_id=b.break_glass_account_id AND e.action='used') AS last_used_at
              FROM break_glass_account b JOIN sys_user u ON u.user_id=b.user_id ORDER BY b.break_glass_account_id
            ) x
            """, cancellationToken: cancellationToken));
        return rows.Select(ParseJson).ToArray();
    }

    public async Task<ProductCommandResult> RegisterAsync(BreakGlassRegisterRequest request, string actor, string? sourceIp, string? traceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CredentialCustodian))
            return new(ProductCommandStatus.Invalid, Message: "credentialCustodian is required");
        await using var connection = connectionFactory.CreateConnection();
        var accountId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO break_glass_account(user_id,credential_custodian,enabled,last_rotated_at)
            SELECT user_id,@Custodian,FALSE,CURRENT_TIMESTAMP FROM sys_user WHERE user_id=@UserId AND status=1
            ON CONFLICT(user_id) DO NOTHING RETURNING break_glass_account_id
            """, new { request.UserId, Custodian = request.CredentialCustodian.Trim() }, cancellationToken: cancellationToken));
        if (!accountId.HasValue) return new(ProductCommandStatus.Conflict, Message: "Active user not found or already registered");
        await RecordEventAsync(connection, accountId.Value, request.UserId, "registered", "succeeded", "Account registered", actor, sourceIp, traceId, cancellationToken);
        return ProductCommandResult.Ok(new { breakGlassAccountId = accountId.Value, enabled = false });
    }

    public async Task<ProductCommandResult> SetStateAsync(long accountId, BreakGlassStateRequest request, string actor, string? sourceIp, string? traceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return new(ProductCommandStatus.Invalid, Message: "reason is required");
        var duration = Math.Clamp(request.DurationMinutes, 5, 60);
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<AccountIdentity>(new CommandDefinition(
            """
            UPDATE break_glass_account SET enabled=@Enabled,
              enabled_until=CASE WHEN @Enabled THEN CURRENT_TIMESTAMP+(@Duration*INTERVAL '1 minute') ELSE NULL END
            WHERE break_glass_account_id=@Id
            RETURNING break_glass_account_id AS AccountId,user_id AS UserId,enabled_until AS EnabledUntil
            """, new { Id = accountId, request.Enabled, Duration = duration }, cancellationToken: cancellationToken));
        if (row is null) return new(ProductCommandStatus.NotFound, Message: "Break-glass account not found");
        await RecordEventAsync(connection, row.AccountId, row.UserId, request.Enabled ? "enabled" : "disabled", "succeeded", request.Reason.Trim(), actor, sourceIp, traceId, cancellationToken);
        return ProductCommandResult.Ok(new { breakGlassAccountId = accountId, request.Enabled, row.EnabledUntil });
    }

    public async Task<ProductCommandResult> ExerciseAsync(long accountId, BreakGlassExerciseRequest request, string actor, string? sourceIp, string? traceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return new(ProductCommandStatus.Invalid, Message: "reason is required");
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<AccountIdentity>(new CommandDefinition(
            """
            UPDATE break_glass_account SET last_exercised_at=CURRENT_TIMESTAMP
            WHERE break_glass_account_id=@Id
            RETURNING break_glass_account_id AS AccountId,user_id AS UserId,enabled_until AS EnabledUntil
            """, new { Id = accountId }, cancellationToken: cancellationToken));
        if (row is null) return new(ProductCommandStatus.NotFound, Message: "Break-glass account not found");
        await RecordEventAsync(connection, row.AccountId, row.UserId, "exercised", request.Successful ? "succeeded" : "failed", request.Reason.Trim(), actor, sourceIp, traceId, cancellationToken);
        return ProductCommandResult.Ok(new { breakGlassAccountId = accountId, successful = request.Successful, exercisedAt = DateTimeOffset.UtcNow });
    }

    public async Task<ProductCommandResult> MarkRotatedAsync(long accountId, string reason, string actor, string? sourceIp, string? traceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason)) return new(ProductCommandStatus.Invalid, Message: "reason is required");
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<AccountIdentity>(new CommandDefinition(
            """
            UPDATE break_glass_account SET last_rotated_at=CURRENT_TIMESTAMP,enabled=FALSE,enabled_until=NULL
            WHERE break_glass_account_id=@Id
            RETURNING break_glass_account_id AS AccountId,user_id AS UserId,enabled_until AS EnabledUntil
            """, new { Id = accountId }, cancellationToken: cancellationToken));
        if (row is null) return new(ProductCommandStatus.NotFound, Message: "Break-glass account not found");
        await RecordEventAsync(connection, row.AccountId, row.UserId, "rotated", "succeeded", reason.Trim(), actor, sourceIp, traceId, cancellationToken);
        return ProductCommandResult.Ok(new { breakGlassAccountId = accountId, rotatedAt = DateTimeOffset.UtcNow });
    }

    public async Task<BreakGlassLoginDecision> AuthorizeLoginAsync(string userName, string? sourceIp, string? traceId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<LoginAccount>(new CommandDefinition(
            """
            SELECT b.break_glass_account_id AS AccountId,b.user_id AS UserId,b.enabled AS Enabled,b.enabled_until AS EnabledUntil
            FROM break_glass_account b JOIN sys_user u ON u.user_id=b.user_id
            WHERE u.user_name=@UserName FOR UPDATE
            """, new { UserName = userName }, transaction, cancellationToken: cancellationToken));
        if (row is null) return BreakGlassLoginDecision.NotBreakGlass;
        if (!row.Enabled || !row.EnabledUntil.HasValue || row.EnabledUntil <= DateTimeOffset.UtcNow)
        {
            await RecordEventAsync(connection, row.AccountId, row.UserId, "used", "blocked", "Account is disabled or activation expired", userName, sourceIp, traceId, cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
            return BreakGlassLoginDecision.Blocked;
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE break_glass_account SET enabled=FALSE,enabled_until=NULL WHERE break_glass_account_id=@Id",
            new { Id = row.AccountId }, transaction, cancellationToken: cancellationToken));
        await RecordEventAsync(connection, row.AccountId, row.UserId, "used", "succeeded", "One-time emergency login", userName, sourceIp, traceId, cancellationToken, transaction);
        await transaction.CommitAsync(cancellationToken);
        return BreakGlassLoginDecision.Allowed;
    }

    private static async Task RecordEventAsync(
        System.Data.IDbConnection connection,long accountId,long userId,string action,string outcome,string reason,string actor,
        string? sourceIp,string? traceId,CancellationToken cancellationToken,System.Data.IDbTransaction? transaction = null)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH recorded AS (
              INSERT INTO break_glass_event(break_glass_account_id,user_id,action,outcome,reason,actor,source_ip,trace_id)
              VALUES(@AccountId,@UserId,@Action,@Outcome,@Reason,@Actor,@SourceIp,@TraceId)
              RETURNING break_glass_event_id)
            INSERT INTO integration_outbox(tenant_id,aggregate_type,aggregate_id,event_type,payload_json)
            SELECT NULL,'break_glass_account',@AccountId::text,'security.break_glass.'||@Action,
              jsonb_build_object('eventId',break_glass_event_id,'accountId',@AccountId,'userId',@UserId,
                'action',@Action,'outcome',@Outcome,'reason',@Reason,'actor',@Actor,'sourceIp',@SourceIp,'traceId',@TraceId)
            FROM recorded
            """, new { AccountId = accountId, UserId = userId, Action = action, Outcome = outcome, Reason = reason, Actor = actor, SourceIp = sourceIp, TraceId = traceId }, transaction, cancellationToken: cancellationToken));
    }

    private static JsonElement ParseJson(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private sealed record AccountIdentity(long AccountId,long UserId,DateTimeOffset? EnabledUntil);
    private sealed record LoginAccount(long AccountId,long UserId,bool Enabled,DateTimeOffset? EnabledUntil);
}

internal enum BreakGlassLoginDecision { NotBreakGlass, Allowed, Blocked }
