using System.Text.Json;
using Aura.Api.MediaAnalysis;

namespace Aura.Api.Graph;

internal sealed class GraphProjectionHostedService(
    GraphProjectionRepository repository,
    IGraphRepository graph,
    GraphRelationshipProjectionService relationships,
    BackgroundWorkerHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<GraphProjectionHostedService> logger) : BackgroundService
{
    private const string WorkerName = "graph-projection";
    private readonly string _workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = Math.Clamp(configuration.GetValue("Graph:Projection:BatchSize", 50), 1, 500);
        var delay = TimeSpan.FromMilliseconds(Math.Max(100, configuration.GetValue("Graph:Projection:PollMilliseconds", 1000)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await repository.ClaimAsync(_workerId, batchSize, TimeSpan.FromMinutes(2), stoppingToken);
                if (messages.Count == 0)
                {
                    await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }
                await heartbeat.SuccessAsync(WorkerName, _workerId, stoppingToken);
                foreach (var message in messages)
                {
                    try
                    {
                        using var metric = GraphMetrics.TrackProjection(message.EventType);
                        using var payload = JsonDocument.Parse(message.PayloadJson);
                        await graph.ProjectEventAsync(payload.RootElement, stoppingToken);
                        await relationships.ProjectAsync(payload.RootElement, stoppingToken);
                        await repository.MarkProcessedAsync(message.OutboxId, stoppingToken);
                        metric.Success();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Graph projection failed. outboxId={OutboxId}", message.OutboxId);
                        await repository.MarkFailureAsync(message, ex, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Graph projection worker iteration failed.");
                await heartbeat.FailureAsync(WorkerName, _workerId, ex, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
