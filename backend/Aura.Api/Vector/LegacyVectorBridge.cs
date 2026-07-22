using System.Text.Json;
using Aura.Api.Ai;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Vector;

internal sealed class LegacyVectorBridge(
    PgSqlConnectionFactory connectionFactory,
    VectorIndexRouter index,
    IConfiguration configuration,
    ILogger<LegacyVectorBridge> logger)
{
    public async Task<AiUpsertResult> UpsertAsync(
        string vid,
        IReadOnlyList<float> feature,
        long? captureId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await ResolveContextAsync(captureId, cancellationToken);
            var embeddingId = await index.UpsertAsync(new VectorIndexDocument(
                context.TenantId,
                context.ModelId,
                vid,
                captureId,
                vid,
                feature,
                JsonSerializer.Serialize(new { source = "legacy-capture", capture_id = captureId }, Aura.Api.MediaAnalysis.MediaAnalysisJson.Options)),
                cancellationToken);
            return new AiUpsertResult(true, $"Vector indexed as {embeddingId?.ToString() ?? vid}.", index.Engine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Legacy vector bridge upsert failed. vid={Vid}, captureId={CaptureId}", vid, captureId);
            return new AiUpsertResult(false, ex.Message, index.Engine);
        }
    }

    public async Task<IReadOnlyList<VectorIndexHit>> SearchAsync(
        IReadOnlyList<float> feature,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveContextAsync(null, cancellationToken);
        return await index.SearchAsync(new VectorIndexQuery(
            context.TenantId,
            context.ModelId,
            feature,
            topK), cancellationToken);
    }

    internal async Task<LegacyVectorContext> ResolveContextAsync(long? captureId, CancellationToken cancellationToken)
    {
        var configuredTenantId = configuration.GetValue<long?>("VectorIndex:DefaultTenantId");
        var modelCode = configuration["VectorIndex:DefaultModelCode"]?.Trim() ?? "legacy-reid";
        var modelVersion = configuration["VectorIndex:DefaultModelVersion"]?.Trim() ?? "default";
        await using var connection = connectionFactory.CreateConnection();
        var tenantId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT COALESCE(
              (SELECT tenant_id FROM capture_record WHERE capture_id=@CaptureId),
              @ConfiguredTenantId,
              (SELECT MIN(tenant_id) FROM tenant_project WHERE enabled=TRUE))
            """,
            new { CaptureId = captureId, ConfiguredTenantId = configuredTenantId },
            cancellationToken: cancellationToken));
        if (!tenantId.HasValue)
        {
            throw new InvalidOperationException("No enabled tenant is available for the legacy vector route.");
        }

        var modelId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO embedding_model(model_code,model_name,model_version,dimension,distance_metric,enabled)
            VALUES(@ModelCode,@ModelCode,@ModelVersion,512,'cosine',TRUE)
            ON CONFLICT(model_code,model_version) DO UPDATE SET enabled=TRUE,updated_at=NOW()
            RETURNING model_id
            """,
            new { ModelCode = Limit(modelCode, 128), ModelVersion = Limit(modelVersion, 64) },
            cancellationToken: cancellationToken));
        return new LegacyVectorContext(tenantId.Value, modelId);
    }

    private static string Limit(string value, int length) => value[..Math.Min(value.Length, length)];
}

internal sealed record LegacyVectorContext(long TenantId, long ModelId);
