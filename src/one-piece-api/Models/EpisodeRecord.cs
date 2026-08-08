using Microsoft.Extensions.VectorData;

namespace OnePieceApi.Models;

/// <summary>
/// Represents an episode record in the One Piece dataset, including its title, overview, arc, and embedding for semantic search.
/// </summary>
public class EpisodeRecord
{
    [VectorStoreKey]
    public ulong Id { get; set; }

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    [VectorStoreData]
    public string Overview { get; set; } = string.Empty;

    [VectorStoreData] 
    public int Season { get; set; }

    [VectorStoreData] 
    public int EpisodeNumber { get; set; }

    [VectorStoreData] 
    public int ReleaseYear { get; set; }

    [VectorStoreData]
    public float Rating { get; set; }

    [VectorStoreVector(Dimensions: EmbeddingSchema.Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> OverviewEmbedding { get; set; }
}