using System.Text.Json;
using System.Text.RegularExpressions;
using Aura.Api.Data;
using Aura.Api.MediaAnalysis;
using Dapper;

namespace Aura.Api.Product;

internal sealed partial class NotificationOrchestrationService(
    PgSqlConnectionFactory connectionFactory,
    IEnumerable<INotificationChannelAdapter> channelAdapters,
    IConfiguration configuration,
    ILogger<NotificationOrchestrationService> logger)
{
    public async Task<IReadOnlyList<object>> ListChannelConfigsAsync(long? tenantId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync(new CommandDefinition(
            """
            SELECT channel_config_id AS channelConfigId,tenant_id AS tenantId,channel,provider_code AS providerCode,
              endpoint_uri AS endpointUri,secret_ref IS NOT NULL AS hasSecret,config_json AS config,status,version,updated_at AS updatedAt
            FROM notification_channel_config WHERE (@TenantId IS NULL OR tenant_id=@TenantId OR tenant_id IS NULL)
            ORDER BY channel,tenant_id NULLS LAST,version DESC
            """, new { TenantId = tenantId }, cancellationToken: cancellationToken));
        return rows.Cast<object>().ToArray();
    }

    public async Task<ProductCommandResult> SaveChannelConfigAsync(NotificationChannelConfigWriteRequest request, string actor, CancellationToken cancellationToken)
    {
        var channel = request.Channel.Trim().ToLowerInvariant();
        if (channel is not ("in_app" or "webhook" or "email" or "sms" or "collaboration" or "ticket" or "web_push"))
            return new(ProductCommandStatus.Invalid, Message: "Unsupported notification channel");
        if (string.IsNullOrWhiteSpace(request.ProviderCode))
            return new(ProductCommandStatus.Invalid, Message: "providerCode is required");
        if (channel is not "in_app" && string.IsNullOrWhiteSpace(request.EndpointUri))
            return new(ProductCommandStatus.Invalid, Message: "External notification channels require endpointUri");
        if (!string.IsNullOrWhiteSpace(request.SecretRef))
        {
            try { SecretReferenceValidator.Validate(request.SecretRef); }
            catch (ArgumentException ex) { return new(ProductCommandStatus.Invalid, Message: ex.Message); }
        }
        await using var connection = connectionFactory.CreateConnection();
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO notification_channel_config(tenant_id,channel,provider_code,endpoint_uri,secret_ref,config_json,status,version,created_by)
            VALUES(@TenantId,@Channel,@Provider,@Endpoint,@SecretRef,@Config::jsonb,'draft',@Version,@Actor)
            ON CONFLICT(tenant_id,channel,provider_code,version) DO UPDATE SET
              endpoint_uri=EXCLUDED.endpoint_uri,secret_ref=EXCLUDED.secret_ref,config_json=EXCLUDED.config_json,
              status='draft',updated_at=CURRENT_TIMESTAMP
            RETURNING channel_config_id
            """, new
            {
                request.TenantId, Channel = channel, Provider = Clean(request.ProviderCode, 128),
                Endpoint = Clean(request.EndpointUri, 2048), request.SecretRef,
                Config = request.Config?.GetRawText() ?? "{}", Version = Math.Max(1, request.Version), Actor = actor
            }, cancellationToken: cancellationToken));
        return ProductCommandResult.Ok(new { channelConfigId = id, status = "draft" });
    }

    public async Task<ProductCommandResult> TransitionChannelConfigAsync(long id, NotificationChannelConfigStateRequest request, CancellationToken cancellationToken)
    {
        var target = request.TargetStatus.Trim().ToLowerInvariant();
        if (target is not ("active" or "disabled" or "draft")) return new(ProductCommandStatus.Invalid, Message: "Channel configuration status is invalid");
        await using var connection = connectionFactory.CreateConnection();
        var count = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notification_channel_config SET status=@Target,updated_at=CURRENT_TIMESTAMP WHERE channel_config_id=@Id AND tenant_id IS NOT DISTINCT FROM @TenantId",
            new { Id = id, request.TenantId, Target = target }, cancellationToken: cancellationToken));
        return count == 0 ? new(ProductCommandStatus.NotFound, Message: "Notification channel configuration not found") : ProductCommandResult.Ok(new { channelConfigId = id, status = target });
    }

    public async Task<ProductCommandResult> QueueAsync(
        NotificationSendRequest request,
        string actor,
        string traceId,
        CancellationToken cancellationToken)
    {
        var channel = request.Channel.Trim().ToLowerInvariant();
        if (channel is not ("in_app" or "webhook" or "email" or "sms" or "collaboration" or "ticket" or "web_push"))
            return new(ProductCommandStatus.Invalid, Message: "Unsupported notification channel");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            return new(ProductCommandStatus.Invalid, Message: "A stable idempotencyKey is required");
        await using var connection = connectionFactory.CreateConnection();
        var template = await connection.QuerySingleOrDefaultAsync<NotificationTemplateRow>(new CommandDefinition(
            """
            SELECT template_code AS TemplateCode,version AS Version,channel AS Channel,content_template AS ContentTemplate,
              masking_policy_json::text AS MaskingPolicyJson FROM notification_template
            WHERE (tenant_id=@TenantId OR tenant_id IS NULL) AND template_code=@Code AND channel=@Channel AND status='active'
            ORDER BY tenant_id NULLS LAST,version DESC LIMIT 1
            """, new { request.TenantId, Code = request.TemplateCode.Trim(), Channel = channel }, cancellationToken: cancellationToken));
        if (template is null) return new(ProductCommandStatus.NotFound, Message: "Active notification template not found");
        var recent = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM notification_delivery WHERE tenant_id=@TenantId AND channel=@Channel
              AND recipient_ref=@Recipient AND created_at>=CURRENT_TIMESTAMP-INTERVAL '1 minute'
            """, new { request.TenantId, Channel = channel, Recipient = request.RecipientRef.Trim() }, cancellationToken: cancellationToken));
        var perMinute = Math.Clamp(configuration.GetValue("CommercialProduct:Notifications:PerRecipientPerMinute", 20), 1, 1000);
        var suppressed = recent >= perMinute;
        var rendered = Render(template.ContentTemplate, request.Payload, template.MaskingPolicyJson);
        var detail = JsonSerializer.Serialize(new
        {
            rendered,
            fallbackChannel = Clean(request.FallbackChannel, 32),
            fallbackRecipient = Clean(request.FallbackRecipient, 256),
            queuedBy = actor,
            suppressionReason = suppressed ? "recipient_rate_limit" : null
        });
        var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            INSERT INTO notification_delivery(tenant_id,case_id,event_id,channel,template_code,template_version,
              recipient_ref,idempotency_key,status,detail_json,payload_json,next_attempt_at,max_attempts,trace_id)
            VALUES(@TenantId,@CaseId,@EventId,@Channel,@Code,@Version,@Recipient,@Key,@Status,@Detail::jsonb,
              @Payload::jsonb,CURRENT_TIMESTAMP,@MaxAttempts,@TraceId)
            ON CONFLICT(tenant_id,channel,idempotency_key) DO NOTHING RETURNING notification_id
            """, new
            {
                request.TenantId,request.CaseId,request.EventId,Channel = channel,Code = template.TemplateCode,template.Version,
                Recipient = request.RecipientRef.Trim(),Key = request.IdempotencyKey.Trim(),Status = suppressed ? "suppressed" : "queued",
                Detail = detail,Payload = request.Payload.GetRawText(),MaxAttempts = Math.Clamp(request.MaxAttempts,1,20),TraceId = traceId
            }, cancellationToken: cancellationToken));
        if (!id.HasValue)
        {
            var prior = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT notification_id FROM notification_delivery WHERE tenant_id=@TenantId AND channel=@Channel AND idempotency_key=@Key",
                new { request.TenantId, Channel = channel, Key = request.IdempotencyKey.Trim() }, cancellationToken: cancellationToken));
            return new(ProductCommandStatus.Duplicate, new { notificationId = prior }, "Notification was already queued");
        }
        return ProductCommandResult.Ok(new { notificationId = id.Value, status = suppressed ? "suppressed" : "queued", renderedPreview = rendered });
    }

    public async Task<ProductCommandResult> ApplyReceiptAsync(
        long notificationId,
        NotificationReceiptRequest request,
        CancellationToken cancellationToken)
    {
        var status = request.Status.Trim().ToLowerInvariant();
        if (status is not ("delivered" or "failed" or "recovered"))
            return new(ProductCommandStatus.Invalid, Message: "Receipt status is invalid");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO notification_receipt(tenant_id,notification_id,provider_receipt_id,status,payload_json)
            SELECT @TenantId,@NotificationId,@Receipt,@Status,@Payload::jsonb FROM notification_delivery
            WHERE tenant_id=@TenantId AND notification_id=@NotificationId
            ON CONFLICT(tenant_id,provider_receipt_id) DO NOTHING
            """, new { request.TenantId, NotificationId = notificationId, Receipt = request.ProviderReceiptId.Trim(), Status = status, Payload = request.Payload.GetRawText() }, transaction, cancellationToken: cancellationToken));
        if (inserted == 0) return new(ProductCommandStatus.Duplicate, Message: "Provider receipt was already applied");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE notification_delivery SET status=@Status,delivered_at=CASE WHEN @Status IN ('delivered','recovered') THEN CURRENT_TIMESTAMP ELSE delivered_at END WHERE tenant_id=@TenantId AND notification_id=@NotificationId",
            new { request.TenantId, NotificationId = notificationId, Status = status }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return ProductCommandResult.Ok(new { notificationId, status });
    }

    internal async Task<NotificationWorkRow?> ClaimAsync(CancellationToken cancellationToken)
    {
        var worker = $"{Environment.MachineName}:{Environment.ProcessId}";
        await using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<NotificationWorkRow>(new CommandDefinition(
            """
            WITH due AS (SELECT notification_id FROM notification_delivery
              WHERE status='queued' AND COALESCE(next_attempt_at,CURRENT_TIMESTAMP)<=CURRENT_TIMESTAMP
                AND (lock_until IS NULL OR lock_until<CURRENT_TIMESTAMP)
              ORDER BY notification_id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE notification_delivery n SET status='sending',locked_by=@Worker,lock_until=CURRENT_TIMESTAMP+INTERVAL '2 minutes',attempt_count=attempt_count+1
            FROM due WHERE n.notification_id=due.notification_id
            RETURNING n.notification_id AS NotificationId,n.tenant_id AS TenantId,n.case_id AS CaseId,n.event_id AS EventId,
              n.channel AS Channel,n.recipient_ref AS RecipientRef,n.idempotency_key AS IdempotencyKey,
              n.attempt_count AS AttemptCount,n.max_attempts AS MaxAttempts,n.detail_json::text AS DetailJson,n.payload_json::text AS PayloadJson,n.trace_id AS TraceId
            """, new { Worker = worker }, cancellationToken: cancellationToken));
    }

    internal async Task DeliverAsync(NotificationWorkRow work, CancellationToken cancellationToken)
    {
        string? error = null;
        try
        {
            var adapter = channelAdapters.FirstOrDefault(candidate => candidate.CanHandle(work.Channel))
                ?? throw new InvalidOperationException($"No notification adapter is registered for channel {work.Channel}");
            using var detail = JsonDocument.Parse(work.DetailJson);
            var rendered = detail.RootElement.TryGetProperty("rendered", out var renderedNode) ? renderedNode.GetString() ?? string.Empty : string.Empty;
            var result = await adapter.DeliverAsync(new NotificationDeliveryContext(
                work.NotificationId,work.TenantId,work.CaseId,work.EventId,work.Channel,work.RecipientRef,
                work.IdempotencyKey,rendered,work.TraceId ?? string.Empty), cancellationToken);
            await CompleteAsync(work.NotificationId, true, null, result.ProviderReceiptId, cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogWarning(ex, "Notification delivery failed. notificationId={NotificationId}", work.NotificationId);
        }
        if (work.AttemptCount < work.MaxAttempts)
        {
            await using var connection = connectionFactory.CreateConnection();
            var seconds = Math.Min(3600, (int)Math.Pow(2, work.AttemptCount) * 10);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE notification_delivery SET status='queued',last_error=@Error,next_attempt_at=CURRENT_TIMESTAMP+(@Seconds*INTERVAL '1 second'),locked_by=NULL,lock_until=NULL WHERE notification_id=@Id",
                new { Error = error, Seconds = seconds, Id = work.NotificationId }, cancellationToken: cancellationToken));
            return;
        }
        await CompleteAsync(work.NotificationId, false, error, null, cancellationToken);
        await QueueFallbackAsync(work, cancellationToken);
    }

    private async Task CompleteAsync(long id,bool success,string? error,string? providerRef,CancellationToken ct)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE notification_delivery SET status=@Status,last_error=@Error,locked_by=NULL,lock_until=NULL,
              provider_ref=COALESCE(@ProviderRef,provider_ref),
              delivered_at=CASE WHEN @Status='delivered' THEN CURRENT_TIMESTAMP ELSE delivered_at END WHERE notification_id=@Id
            """, new { Id = id, Status = success ? "delivered" : "failed", Error = error, ProviderRef = providerRef }, cancellationToken: ct));
    }

    private async Task QueueFallbackAsync(NotificationWorkRow work,CancellationToken ct)
    {
        using var detail = JsonDocument.Parse(work.DetailJson);
        var root = detail.RootElement;
        var channel = root.TryGetProperty("fallbackChannel",out var channelNode) ? channelNode.GetString() : null;
        var recipient = root.TryGetProperty("fallbackRecipient",out var recipientNode) ? recipientNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(recipient)) return;
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO notification_delivery(tenant_id,case_id,event_id,channel,template_code,template_version,recipient_ref,
              idempotency_key,status,detail_json,payload_json,next_attempt_at,max_attempts,trace_id)
            SELECT tenant_id,case_id,event_id,@Channel,template_code,template_version,@Recipient,
              idempotency_key||':fallback','queued',detail_json||jsonb_build_object('fallbackOf',notification_id),payload_json,CURRENT_TIMESTAMP,max_attempts,trace_id
            FROM notification_delivery WHERE notification_id=@Id
            ON CONFLICT(tenant_id,channel,idempotency_key) DO NOTHING
            """, new { Id = work.NotificationId, Channel = channel.Trim().ToLowerInvariant(), Recipient = recipient.Trim() }, cancellationToken: ct));
    }

    private static string Render(string template,JsonElement payload,string maskingPolicyJson)
    {
        var masked = ParseMaskedFields(maskingPolicyJson);
        return TemplateTokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (masked.Contains(key)) return "***";
            return payload.ValueKind==JsonValueKind.Object && payload.TryGetProperty(key,out var value)
                ? value.ValueKind==JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText()
                : string.Empty;
        });
    }
    private static HashSet<string> ParseMaskedFields(string json)
    {
        try
        {
            using var document=JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("maskedFields",out var node) && node.ValueKind==JsonValueKind.Array
                ? node.EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String).Select(item=>item.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch { return []; }
    }
    private static string? Clean(string? value,int max) => string.IsNullOrWhiteSpace(value)?null:value.Trim()[..Math.Min(value.Trim().Length,max)];
    [GeneratedRegex("\\{\\{([A-Za-z0-9_.-]{1,64})\\}\\}")]
    private static partial Regex TemplateTokenPattern();

    private sealed record NotificationTemplateRow(string TemplateCode,int Version,string Channel,string ContentTemplate,string MaskingPolicyJson);
}

internal sealed record NotificationWorkRow(long NotificationId,long TenantId,long? CaseId,long? EventId,string Channel,string RecipientRef,string IdempotencyKey,int AttemptCount,int MaxAttempts,string DetailJson,string PayloadJson,string? TraceId);

internal sealed class NotificationDeliveryWorker(
    NotificationOrchestrationService notifications,
    IConfiguration configuration,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CommercialProduct:Notifications:WorkerEnabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var work=await notifications.ClaimAsync(stoppingToken);
                if(work is null){await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken);continue;}
                await notifications.DeliverAsync(work,stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Notification worker iteration failed");await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken);}
        }
    }
}
