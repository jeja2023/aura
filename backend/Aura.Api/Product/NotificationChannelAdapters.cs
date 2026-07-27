using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aura.Api.Data;
using Aura.Api.MediaAnalysis;
using Dapper;

namespace Aura.Api.Product;

internal sealed record NotificationDeliveryContext(
    long NotificationId,long TenantId,long? CaseId,long? EventId,string Channel,string RecipientRef,
    string IdempotencyKey,string RenderedContent,string TraceId);

internal sealed record NotificationAdapterResult(string? ProviderReceiptId = null, string? ExternalTicketNo = null);

internal interface INotificationChannelAdapter
{
    bool CanHandle(string channel);
    Task<NotificationAdapterResult> DeliverAsync(NotificationDeliveryContext context, CancellationToken cancellationToken);
}

internal sealed class InAppNotificationChannelAdapter : INotificationChannelAdapter
{
    public bool CanHandle(string channel) => channel.Equals("in_app", StringComparison.OrdinalIgnoreCase);
    public Task<NotificationAdapterResult> DeliverAsync(NotificationDeliveryContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new NotificationAdapterResult($"in-app:{context.NotificationId}"));
}

internal sealed class WebhookNotificationChannelAdapter(
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    IHttpClientFactory httpClientFactory) : INotificationChannelAdapter
{
    public bool CanHandle(string channel) => channel.Equals("webhook", StringComparison.OrdinalIgnoreCase);

    public async Task<NotificationAdapterResult> DeliverAsync(NotificationDeliveryContext context, CancellationToken cancellationToken)
    {
        var uri = await outboundUrlPolicy.ValidateArtifactUriAsync(context.RecipientRef, cancellationToken);
        var client = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                notificationId = context.NotificationId,
                context.TenantId,
                context.CaseId,
                context.EventId,
                content = context.RenderedContent,
                context.TraceId
            })
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", context.IdempotencyKey);
        message.Headers.TryAddWithoutValidation("X-Aura-Trace-Id", context.TraceId);
        using var response = await client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Webhook returned {(int)response.StatusCode}");
        return new(response.Headers.TryGetValues("X-Provider-Receipt-Id", out var values) ? values.FirstOrDefault() : null);
    }
}

internal sealed class ConfiguredHttpNotificationChannelAdapter(
    PgSqlConnectionFactory connectionFactory,
    MediaAnalysisOutboundUrlPolicy outboundUrlPolicy,
    ISecretReferenceResolver secretResolver,
    IHttpClientFactory httpClientFactory) : INotificationChannelAdapter
{
    private static readonly HashSet<string> Channels = new(["email", "sms", "collaboration", "ticket", "web_push"], StringComparer.OrdinalIgnoreCase);

    public bool CanHandle(string channel) => Channels.Contains(channel);

    public async Task<NotificationAdapterResult> DeliverAsync(NotificationDeliveryContext context, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var config = await connection.QuerySingleOrDefaultAsync<ChannelConfigRow>(new CommandDefinition(
            """
            SELECT channel_config_id AS ChannelConfigId,provider_code AS ProviderCode,endpoint_uri AS EndpointUri,
              secret_ref AS SecretRef,config_json::text AS ConfigJson
            FROM notification_channel_config
            WHERE (tenant_id=@TenantId OR tenant_id IS NULL) AND channel=@Channel AND status='active'
            ORDER BY tenant_id NULLS LAST,version DESC LIMIT 1
            """, new { context.TenantId, Channel = context.Channel }, cancellationToken: cancellationToken));
        if (config is null || string.IsNullOrWhiteSpace(config.EndpointUri))
            throw new InvalidOperationException($"No active {context.Channel} provider configuration exists for tenant {context.TenantId}");
        var uri = await outboundUrlPolicy.ValidateArtifactUriAsync(config.EndpointUri, cancellationToken);
        var secret = string.IsNullOrWhiteSpace(config.SecretRef) ? null : await secretResolver.ResolveAsync(config.SecretRef, cancellationToken);
        using var configDocument = JsonDocument.Parse(config.ConfigJson);
        object? subscriptions = null;
        if (context.Channel.Equals("web_push", StringComparison.OrdinalIgnoreCase))
        {
            var rows = (await connection.QueryAsync<MobilePushRow>(new CommandDefinition(
                """
                SELECT endpoint_uri AS EndpointUri,key_p256dh AS KeyP256dh,key_auth AS KeyAuth
                FROM mobile_push_subscription
                WHERE tenant_id=@TenantId AND user_name=@User AND status='active'
                ORDER BY subscription_id
                """, new { context.TenantId, User = context.RecipientRef }, cancellationToken: cancellationToken))).AsList();
            if (rows.Count == 0)
                throw new InvalidOperationException($"No active Web Push subscription exists for recipient {context.RecipientRef}");
            subscriptions = rows.Select(row => new { endpoint = row.EndpointUri, keys = new { p256dh = row.KeyP256dh, auth = row.KeyAuth } }).ToArray();
        }
        var client = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                notificationId = context.NotificationId,
                channel = context.Channel,
                provider = config.ProviderCode,
                recipient = context.RecipientRef,
                content = context.RenderedContent,
                context.TenantId,
                context.CaseId,
                context.EventId,
                context.TraceId,
                providerOptions = configDocument.RootElement,
                subscriptions
            })
        };
        if (!string.IsNullOrWhiteSpace(secret)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", context.IdempotencyKey);
        message.Headers.TryAddWithoutValidation("X-Aura-Trace-Id", context.TraceId);
        using var response = await client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{context.Channel} provider returned {(int)response.StatusCode}");

        var result = await ReadResultAsync(response, cancellationToken);
        var receipt = result.TryGetProperty("receiptId", out var receiptNode) ? receiptNode.GetString() : null;
        var ticket = context.Channel == "ticket" && result.TryGetProperty("ticketId", out var ticketNode) ? ticketNode.GetString() : null;
        if (context.CaseId.HasValue && !string.IsNullOrWhiteSpace(ticket))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE incident_case SET external_ticket_no=COALESCE(external_ticket_no,@Ticket),version=version+1,updated_at=CURRENT_TIMESTAMP WHERE tenant_id=@TenantId AND case_id=@CaseId",
                new { context.TenantId, context.CaseId, Ticket = ticket }, cancellationToken: cancellationToken));
        }
        return new(receipt, ticket);
    }

    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            return result.ValueKind == JsonValueKind.Object ? result : JsonSerializer.SerializeToElement(new { });
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private sealed record ChannelConfigRow(long ChannelConfigId,string ProviderCode,string? EndpointUri,string? SecretRef,string ConfigJson);
    private sealed record MobilePushRow(string EndpointUri,string KeyP256dh,string KeyAuth);
}
