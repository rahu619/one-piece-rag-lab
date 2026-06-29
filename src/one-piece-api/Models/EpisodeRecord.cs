using Microsoft.Extensions.VectorData;

namespace OnePieceApi.Models;

/// <summary>
/// Represents an episode record in the One Piece dataset, including its title, overview, arc, and embedding for semantic search.
/// </summary>
public sealed record EpisodeRecord
{
    /// <summary>
    /// The unique identifier for the episode record, used as the key in the vector store.
    /// </summary>
    [VectorStoreKey]
    public ulong Id { get; init; }

    /// <summary>
    /// The title of the episode, indexed for search.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// The overview or summary of the episode, indexed for full-text search.
    /// </summary>
    [VectorStoreData(IsFullTextIndexed = true)]
    public string Overview { get; init; } = string.Empty;

    /// <summary>
    /// The arc or story arc to which the episode belongs, indexed for search.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string Arc { get; init; } = string.Empty;

    /// <summary>
    /// The embedding vector for the episode overview, used for semantic search and similarity comparisons.
    /// The embedding is generated using a pre-trained model and stored as a read-only memory of floats.    
    /// </summary>
    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> OverviewEmbedding { get; init; }
}