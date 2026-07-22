namespace Aura.Api.MediaAnalysis;

internal sealed class MediaAnalysisJobHostedService(
    MediaAnalysisRepository repository,
    MediaAnalysisOrchestrator orchestrator,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<MediaAnalysisJobHostedService> logger) : BackgroundService
{
    private const string WorkerName = "media-analysis-job";
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromMilliseconds(Math.Max(100, configuration.GetValue("MediaAnalysis:Workers:JobPollMilliseconds", 1000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await repository.ClaimJobAsync(TimeSpan.FromMinutes(2), stoppingToken);
                if (job is null)
                {
                    await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
                    await Task.Delay(idleDelay, stoppingToken);
                    continue;
                }

                try
                {
                    await orchestrator.SubmitJobAsync(job, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Media-analysis job submission failed. jobId={JobId}", job.JobId);
                    await repository.MarkJobFailureAsync(job.JobId, ex.Message, stoppingToken);
                }
                await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media-analysis job worker iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _instanceId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

internal sealed class SubscriptionReconcilerHostedService(
    MediaAnalysisRepository repository,
    MediaAnalysisOrchestrator orchestrator,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<SubscriptionReconcilerHostedService> logger) : BackgroundService
{
    private const string WorkerName = "media-analysis-subscription";
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("MediaAnalysis:Workers:SubscriptionPollSeconds", 5)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var subscriptions = await repository.ClaimSubscriptionsAsync(25, TimeSpan.FromMinutes(1), stoppingToken);
                foreach (var subscription in subscriptions)
                {
                    try
                    {
                        await orchestrator.ReconcileSubscriptionAsync(subscription, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Stream subscription reconciliation failed. subscriptionId={SubscriptionId}", subscription.SubscriptionId);
                        await repository.MarkSubscriptionFailureAsync(subscription.SubscriptionId, ex.Message, stoppingToken);
                    }
                }

                await heartbeat.SuccessAsync(WorkerName, _instanceId, stoppingToken);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stream subscription reconciler iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _instanceId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

internal sealed class InboxProcessorHostedService(
    InboxRepository repository,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<InboxProcessorHostedService> logger) : BackgroundService
{
    private const string WorkerName = "media-analysis-inbox";
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = Math.Clamp(configuration.GetValue("MediaAnalysis:Workers:InboxBatchSize", 50), 1, 500);
        var idleDelay = TimeSpan.FromMilliseconds(Math.Max(100, configuration.GetValue("MediaAnalysis:Workers:InboxPollMilliseconds", 500)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await repository.ClaimAsync(_workerId, batchSize, TimeSpan.FromMinutes(2), stoppingToken);
                if (messages.Count == 0)
                {
                    await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
                    await Task.Delay(idleDelay, stoppingToken);
                    continue;
                }
                await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);

                foreach (var message in messages)
                {
                    try
                    {
                        await repository.ProcessAsync(message, stoppingToken);
                        MediaAnalysisMetrics.ObserveInbox(message, "success");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Inbox event processing failed. inboxId={InboxId}, eventType={EventType}", message.InboxId, message.EventType);
                        MediaAnalysisMetrics.ObserveInbox(message, "failure");
                        await repository.MarkFailureAsync(message.InboxId, message.AttemptCount, ex, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox worker iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _workerId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
