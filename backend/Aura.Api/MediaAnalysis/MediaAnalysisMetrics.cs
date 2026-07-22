using Prometheus;

namespace Aura.Api.MediaAnalysis;

internal static class MediaAnalysisMetrics
{
    private static readonly Counter WebhookEvents = Metrics.CreateCounter(
        "aura_media_webhook_events_total",
        "Media-analysis webhook events by provider and result.",
        new CounterConfiguration { LabelNames = ["provider", "result"] });
    private static readonly Counter WebhookAuthenticationFailures = Metrics.CreateCounter(
        "aura_media_webhook_auth_failures_total",
        "Media-analysis webhook authentication failures.",
        new CounterConfiguration { LabelNames = ["reason"] });
    private static readonly Counter InboxProcessed = Metrics.CreateCounter(
        "aura_media_inbox_processed_total",
        "Inbox processing outcomes.",
        new CounterConfiguration { LabelNames = ["event_type", "result"] });
    private static readonly Counter ProviderOperations = Metrics.CreateCounter(
        "aura_media_provider_operations_total",
        "Outbound media-analysis provider operations.",
        new CounterConfiguration { LabelNames = ["provider", "operation", "result"] });
    private static readonly Histogram ProviderDuration = Metrics.CreateHistogram(
        "aura_media_provider_operation_duration_seconds",
        "Outbound media-analysis provider operation latency.",
        new HistogramConfiguration
        {
            LabelNames = ["provider", "operation"],
            Buckets = Histogram.ExponentialBuckets(0.025, 2, 14)
        });
    private static readonly Histogram InboxTransportDelay = Metrics.CreateHistogram(
        "aura_media_inbox_transport_delay_seconds",
        "Delay between provider production and Aura receipt.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.01, 2, 18) });
    private static readonly Histogram InboxProcessingDelay = Metrics.CreateHistogram(
        "aura_media_inbox_processing_delay_seconds",
        "Delay between Aura receipt and processing attempt completion.",
        new HistogramConfiguration { LabelNames = ["result"], Buckets = Histogram.ExponentialBuckets(0.01, 2, 18) });
    private static readonly Counter JobOutcomes = Metrics.CreateCounter(
        "aura_media_job_outcomes_total",
        "Media-analysis job terminal outcomes.",
        new CounterConfiguration { LabelNames = ["media_type", "state"] });
    private static readonly Histogram JobDuration = Metrics.CreateHistogram(
        "aura_media_job_duration_seconds",
        "Media-analysis job duration from creation to a terminal outcome.",
        new HistogramConfiguration
        {
            LabelNames = ["media_type", "state"],
            Buckets = Histogram.ExponentialBuckets(0.1, 2, 20)
        });

    internal static void ObserveWebhook(string provider, MediaAnalysisWebhookResult result)
    {
        WebhookEvents.WithLabels(provider, "accepted").Inc(result.Accepted);
        WebhookEvents.WithLabels(provider, "duplicate").Inc(result.Duplicate);
        WebhookEvents.WithLabels(provider, "rejected").Inc(result.Rejected);
    }

    internal static void ObserveAuthenticationFailure(Exception exception) =>
        WebhookAuthenticationFailures.WithLabels(exception.Message switch
        {
            var message when message.Contains("timestamp", StringComparison.OrdinalIgnoreCase) => "timestamp",
            var message when message.Contains("nonce", StringComparison.OrdinalIgnoreCase) => "nonce",
            var message when message.Contains("signature", StringComparison.OrdinalIgnoreCase) => "signature",
            _ => "credentials"
        }).Inc();

    internal static void ObserveInbox(MediaAnalysisInboxRecord message, string result)
    {
        InboxProcessed.WithLabels(message.EventType, result).Inc();
        var now = DateTime.UtcNow;
        InboxProcessingDelay.WithLabels(result).Observe(Math.Max(0, (now - NormalizeUtc(message.ReceivedAt)).TotalSeconds));
        if (message.ProducedAt.HasValue)
            InboxTransportDelay.Observe(Math.Max(0, (NormalizeUtc(message.ReceivedAt) - NormalizeUtc(message.ProducedAt.Value)).TotalSeconds));
    }

    internal static void ObserveJobOutcome(MediaAnalysisJobRecord job, string state)
    {
        if (state is not ("completed" or "failed" or "cancelled" or "rejected")) return;
        JobOutcomes.WithLabels(job.MediaType, state).Inc();
        JobDuration.WithLabels(job.MediaType, state)
            .Observe(Math.Max(0, (DateTime.UtcNow - NormalizeUtc(job.CreatedAt)).TotalSeconds));
    }

    internal static ProviderTimer TrackProvider(string provider, string operation) => new(provider, operation);

    private static DateTime NormalizeUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal sealed class ProviderTimer(string provider, string operation) : IDisposable
    {
        private readonly IDisposable _timer = ProviderDuration.WithLabels(provider, operation).NewTimer();
        private string _result = "failure";

        internal void Success() => _result = "success";
        internal void Failure(Exception exception, bool callerCancelled)
        {
            _result = callerCancelled
                ? "cancelled"
                : exception is TimeoutException or TaskCanceledException
                  || exception.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                    ? "timeout"
                    : "failure";
        }

        public void Dispose()
        {
            _timer.Dispose();
            ProviderOperations.WithLabels(provider, operation, _result).Inc();
        }
    }
}
