using System.Globalization;
using Aura.Api.Data;
using Dapper;

namespace Aura.Api.Vector;

internal sealed class PgVectorIndex(PgSqlConnectionFactory connectionFactory) : IVectorIndex
{
    public string Engine => "pgvector";

    public async Task<long?> UpsertAsync(VectorIndexDocument document, CancellationToken cancellationToken)
    {
        var normalized = VectorValidation.Normalize(document.Feature);
        var vector = VectorValidation.ToSqlLiteral(normalized);
        await using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO feature_embedding(tenant_id,external_embedding_id,vid,capture_id,model_id,feature,metadata_json)
            VALUES(@TenantId,@ExternalEmbeddingId,@Vid,@CaptureId,@ModelId,CAST(@Feature AS vector),CAST(@MetadataJson AS jsonb))
            ON CONFLICT(tenant_id,model_id,vid,capture_id) DO UPDATE SET
              external_embedding_id=EXCLUDED.external_embedding_id,feature=EXCLUDED.feature,
              metadata_json=EXCLUDED.metadata_json,updated_at=NOW()
            RETURNING embedding_id
            """,
            new
            {
                document.TenantId,
                document.ModelId,
                Vid = document.Vid.Trim(),
                document.CaptureId,
                document.ExternalEmbeddingId,
                Feature = vector,
                MetadataJson = string.IsNullOrWhiteSpace(document.MetadataJson) ? "{}" : document.MetadataJson
            }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<VectorIndexHit>> SearchAsync(VectorIndexQuery query, CancellationToken cancellationToken)
    {
        var normalized = VectorValidation.Normalize(query.Feature);
        var vector = VectorValidation.ToSqlLiteral(normalized);
        var topK = Math.Clamp(query.TopK, 1, 200);
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PgVectorHit>(new CommandDefinition(
            """
            WITH candidates AS (
              SELECT embedding_id AS EmbeddingId, vid AS Vid, capture_id AS CaptureId,
                external_embedding_id AS ExternalEmbeddingId, metadata_json::text AS MetadataJson,
                1-(feature <=> CAST(@Feature AS vector)) AS Score
              FROM feature_embedding
              WHERE tenant_id=@TenantId AND model_id=@ModelId AND (@Vid IS NULL OR vid=@Vid)
              ORDER BY feature <=> CAST(@Feature AS vector)
              LIMIT @CandidateLimit
            )
            SELECT * FROM candidates WHERE (@MinScore IS NULL OR Score>=@MinScore)
            ORDER BY Score DESC, EmbeddingId LIMIT @TopK
            """,
            new
            {
                query.TenantId,
                query.ModelId,
                Feature = vector,
                Vid = string.IsNullOrWhiteSpace(query.Vid) ? null : query.Vid.Trim(),
                query.MinScore,
                CandidateLimit = Math.Max(topK, Math.Min(1000, topK * 4)),
                TopK = topK
            }, cancellationToken: cancellationToken));
        return rows.Select(x => new VectorIndexHit(
            x.EmbeddingId, x.Vid, x.Score, x.CaptureId, x.ExternalEmbeddingId, x.MetadataJson, Engine)).ToList();
    }

    public async Task<VectorIndexStatus> GetStatusAsync(long? tenantId, long? modelId, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            var row = await connection.QuerySingleAsync<PgVectorStatus>(new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname='vector') AS ExtensionAvailable,
                  (SELECT COUNT(*) FROM feature_embedding
                   WHERE (@TenantId IS NULL OR tenant_id=@TenantId) AND (@ModelId IS NULL OR model_id=@ModelId)) AS Count
                """,
                new { TenantId = tenantId, ModelId = modelId }, cancellationToken: cancellationToken));
            return new VectorIndexStatus(Engine, row.ExtensionAvailable, row.Count, row.ExtensionAvailable ? null : "vector extension is not installed");
        }
        catch (Exception ex)
        {
            return new VectorIndexStatus(Engine, false, 0, ex.Message);
        }
    }

    private sealed record PgVectorHit(long EmbeddingId, string Vid, long? CaptureId, string? ExternalEmbeddingId, string MetadataJson, double Score);
    private sealed record PgVectorStatus(bool ExtensionAvailable, long Count);
}

internal static class VectorValidation
{
    public const int Dimension = 512;

    public static void Validate(IReadOnlyList<float> feature)
    {
        if (feature.Count != Dimension) throw new ArgumentException($"Vector dimension must be {Dimension}.");
        if (feature.Any(x => !float.IsFinite(x))) throw new ArgumentException("Vector contains a non-finite value.");
        var norm = Math.Sqrt(feature.Sum(x => (double)x * x));
        if (norm <= double.Epsilon) throw new ArgumentException("Vector norm must be greater than zero.");
    }

    public static float[] Normalize(IReadOnlyList<float> feature)
    {
        Validate(feature);
        var norm = Math.Sqrt(feature.Sum(x => (double)x * x));
        return feature.Select(value => (float)(value / norm)).ToArray();
    }

    public static string ToSqlLiteral(IReadOnlyList<float> feature) =>
        "[" + string.Join(',', feature.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
