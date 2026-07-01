using Microsoft.Extensions.VectorData;

namespace OnePieceApi.Models;

/// <summary>
/// Represents a cached query and its corresponding answer and references.
/// </summary>
public class CacheRecord
{
    [VectorStoreKey]
    public ulong Id { get; set; }

    [VectorStoreData]
    public string Query { get; set; } = string.Empty;

    [VectorStoreData]
    public string Answer { get; set; } = string.Empty;

    [VectorStoreData]
    public string SourcesJson { get; set; } = string.Empty;

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> QueryEmbedding { get; set; }
}
