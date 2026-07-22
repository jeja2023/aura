namespace Aura.Api.Vector;

internal sealed record VectorIndexDocument(
    long TenantId,
    long ModelId,
    string Vid,
    long? CaptureId,
    string? ExternalEmbeddingId,
    IReadOnlyList<float> Feature,
    string MetadataJson);

internal sealed record VectorIndexQuery(
    long TenantId,
    long ModelId,
    IReadOnlyList<float> Feature,
    int TopK,
    double? MinScore = null,
    string? Vid = null);

internal sealed record VectorIndexHit(
    long? EmbeddingId,
    string Vid,
    double Score,
    long? CaptureId,
    string? ExternalEmbeddingId,
    string MetadataJson,
    string Engine);

internal sealed record VectorIndexStatus(string Engine, bool Available, long Count, string? Detail = null);

internal interface IVectorIndex
{
    string Engine { get; }
    Task<long?> UpsertAsync(VectorIndexDocument document, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorIndexHit>> SearchAsync(VectorIndexQuery query, CancellationToken cancellationToken);
    Task<VectorIndexStatus> GetStatusAsync(long? tenantId, long? modelId, CancellationToken cancellationToken);
}

internal sealed record VectorUpsertRequest(
    long TenantId,
    long ModelId,
    string Vid,
    long? CaptureId,
    string? ExternalEmbeddingId,
    IReadOnlyList<float> Feature,
    System.Text.Json.JsonElement? Metadata);

internal sealed record VectorIndexSearchRequest(
    long TenantId,
    long ModelId,
    IReadOnlyList<float> Feature,
    int TopK = 10,
    double? MinScore = null,
    string? Vid = null);
