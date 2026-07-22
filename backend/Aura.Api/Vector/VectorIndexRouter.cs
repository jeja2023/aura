using Aura.Api.Ai;

namespace Aura.Api.Vector;

internal sealed class LegacyArangoVectorIndex(AiClient aiClient) : IVectorIndex
{
    public string Engine => "legacy-arangodb";

    public async Task<long?> UpsertAsync(VectorIndexDocument document, CancellationToken cancellationToken)
    {
        VectorValidation.Validate(document.Feature);
        var result = await aiClient.UpsertAsync(document.Vid, document.Feature.ToList());
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return null;
    }

    public async Task<IReadOnlyList<VectorIndexHit>> SearchAsync(VectorIndexQuery query, CancellationToken cancellationToken)
    {
        VectorValidation.Validate(query.Feature);
        var result = await aiClient.SearchAsync(query.Feature.ToList(), Math.Clamp(query.TopK, 1, 50));
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return result.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.vid) && (!query.MinScore.HasValue || x.score >= query.MinScore.Value))
            .Select(x => new VectorIndexHit(null, x.vid!, x.score, null, null, "{}", Engine))
            .ToList();
    }

    public async Task<VectorIndexStatus> GetStatusAsync(long? tenantId, long? modelId, CancellationToken cancellationToken)
    {
        var result = await aiClient.GetSearchStatsAsync(15);
        return new VectorIndexStatus(Engine, result.Success, 0, result.Success ? null : result.Message);
    }
}

internal sealed class VectorIndexRouter(
    PgVectorIndex pgVector,
    LegacyArangoVectorIndex legacy,
    VectorWriteCompensationRepository compensationRepository,
    IConfiguration configuration,
    ILogger<VectorIndexRouter> logger) : IVectorIndex
{
    public string Engine => configuration["VectorIndex:ReadEngine"]?.Trim().ToLowerInvariant() == "legacy-arangodb"
        ? legacy.Engine
        : pgVector.Engine;

    public async Task<long?> UpsertAsync(VectorIndexDocument document, CancellationToken cancellationToken)
    {
        var writeMode = configuration["VectorIndex:WriteMode"]?.Trim().ToLowerInvariant() ?? "pgvector";
        if (writeMode == "legacy-arangodb") return await legacy.UpsertAsync(document, cancellationToken);

        var id = await pgVector.UpsertAsync(document, cancellationToken);
        if (writeMode == "dual")
        {
            try
            {
                await legacy.UpsertAsync(document, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Legacy vector dual-write failed. tenantId={TenantId}, modelId={ModelId}, vid={Vid}", document.TenantId, document.ModelId, document.Vid);
                if (id.HasValue) await compensationRepository.EnqueueAsync(id.Value, ex, cancellationToken);
            }
        }
        return id;
    }

    public async Task<IReadOnlyList<VectorIndexHit>> SearchAsync(VectorIndexQuery query, CancellationToken cancellationToken)
    {
        if (Engine == legacy.Engine) return await SearchWithMetricsAsync(legacy, query, cancellationToken);
        try
        {
            return await SearchWithMetricsAsync(pgVector, query, cancellationToken);
        }
        catch (Exception ex) when (configuration.GetValue("VectorIndex:AllowLegacyReadFallback", true))
        {
            logger.LogWarning(ex, "pgvector search failed; using the configured legacy read fallback.");
            return await SearchWithMetricsAsync(legacy, query, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<VectorIndexHit>> SearchWithMetricsAsync(
        IVectorIndex index,
        VectorIndexQuery query,
        CancellationToken cancellationToken)
    {
        using var metric = VectorIndexMetrics.Track(index.Engine);
        var result = await index.SearchAsync(query, cancellationToken);
        metric.Success(result.Count);
        return result;
    }

    public Task<VectorIndexStatus> GetStatusAsync(long? tenantId, long? modelId, CancellationToken cancellationToken) =>
        Engine == legacy.Engine
            ? legacy.GetStatusAsync(tenantId, modelId, cancellationToken)
            : pgVector.GetStatusAsync(tenantId, modelId, cancellationToken);
}
