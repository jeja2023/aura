using Aura.Api.Models;
using Aura.Api.Ai;
using Aura.Api.Data;

namespace Aura.Api.Clustering;

internal sealed class ClusterApplicationService
{
    private readonly AppStore _store;
    private readonly PgSqlStore _db;
    private readonly AiClient _aiClient;
    private readonly FeatureClusteringService _clusterService;

    public ClusterApplicationService(AppStore store, PgSqlStore db, AiClient aiClient, FeatureClusteringService clusterService)
    {
        _store = store;
        _db = db;
        _aiClient = aiClient;
        _clusterService = clusterService;
    }

    public async Task<object> RunAsync(ClusterRunReq req)
    {
        var gapMinutes = req.GapMinutes <= 0 ? 30 : req.GapMinutes;
        var maxCaptures = req.MaxCaptures <= 0 ? 500 : Math.Min(req.MaxCaptures, 1000);
        var similarityThreshold = req.SimilarityThreshold <= 0 ? 0.82d : req.SimilarityThreshold;
        var minPoints = req.MinPoints <= 0 ? 2 : req.MinPoints;

        var source = await _db.GetCapturesAsync(maxCaptures);
        var captures = source.Count > 0
            ? source.Select(x => new CaptureClusterSource(x.CaptureId, x.DeviceId, x.ChannelNo, new DateTimeOffset(x.CaptureTime), x.MetadataJson, x.ImagePath)).ToList()
            : _store.Captures.Select(x => new CaptureClusterSource(x.CaptureId, x.DeviceId, x.ChannelNo, x.CaptureTime, x.MetadataJson, x.ImagePath)).ToList();

        var featurePoints = await BuildClusterPointsAsync(captures);
        var clusterResult = featurePoints.Count >= minPoints
            ? _clusterService.ClusterByFeatures(featurePoints, similarityThreshold, minPoints)
            : _clusterService.ClusterByTemporalWindow(captures, gapMinutes);
        if (clusterResult.ClusterCount == 0 && captures.Count > 0)
        {
            clusterResult = _clusterService.ClusterByTemporalWindow(captures, gapMinutes);
        }

        var results = clusterResult.Groups.Select(group => new VirtualPersonEntity(
            Vid: FeatureClusteringService.BuildVirtualPersonId(group),
            FirstSeen: group.Members.Min(r => r.CaptureTime),
            LastSeen: group.Members.Max(r => r.CaptureTime),
            DeviceId: group.MainDeviceId,
            CaptureCount: group.Members.Count,
            ClusterAlgorithm: clusterResult.Algorithm,
            ClusterScore: group.CohesionScore)).ToList();

        var cleared = await _db.ClearVirtualPersonsAsync();
        if (cleared)
        {
            foreach (var item in results)
            {
                await _db.InsertVirtualPersonAsync(item.Vid, item.FirstSeen, item.LastSeen, item.DeviceId, item.CaptureCount);
            }
        }
        else
        {
            _store.VirtualPersons.Clear();
            _store.VirtualPersons.AddRange(results);
        }

        var detail = $"algorithm={clusterResult.Algorithm}, candidates={clusterResult.CandidateCount}, features={clusterResult.FeatureCount}, clusters={clusterResult.ClusterCount}, noise={clusterResult.NoiseCount}";
        await _db.InsertOperationAsync("系统任务", "聚类执行", detail);
        _store.Operations.Add(new OperationEntity(
            OperationId: Interlocked.Increment(ref _store.OperationSeed),
            OperatorName: "系统任务",
            Action: "聚类执行",
            Detail: detail,
            CreatedAt: DateTimeOffset.Now));

        return new
        {
            count = results.Count,
            gapMinutes,
            algorithm = clusterResult.Algorithm,
            candidates = clusterResult.CandidateCount,
            features = clusterResult.FeatureCount,
            clusters = clusterResult.ClusterCount,
            noise = clusterResult.NoiseCount,
            similarityThreshold,
            minPoints
        };
    }

    private async Task<List<CaptureClusterPoint>> BuildClusterPointsAsync(IReadOnlyList<CaptureClusterSource> captures)
    {
        var candidates = captures
            .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var gate = new SemaphoreSlim(4);
        var tasks = candidates.Select(async capture =>
        {
            await gate.WaitAsync();
            try
            {
                var ai = await _aiClient.ExtractByPathAsync(capture.ImagePath!, capture.MetadataJson);
                if (!ai.Success || ai.Feature.Count != 512)
                {
                    return null;
                }

                return new CaptureClusterPoint(capture, ai.Feature);
            }
            finally
            {
                gate.Release();
            }
        });

        var resolved = await Task.WhenAll(tasks);
        return resolved
            .Where(x => x is not null)
            .Cast<CaptureClusterPoint>()
            .OrderBy(x => x.Capture.CaptureTime)
            .ThenBy(x => x.Capture.CaptureId)
            .ToList();
    }
}

