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

    [VectorStoreData] // Filterable payload field in Qdrant
    public int Season { get; set; }

    [VectorStoreData] // Filterable payload field in Qdrant
    public int EpisodeNumber { get; set; }

    [VectorStoreData] // Filterable payload field in Qdrant
    public int ReleaseYear { get; set; }

    [VectorStoreData] // Filterable payload field in Qdrant
    public float Rating { get; set; }

    [VectorStoreVector(1536)]
    public ReadOnlyMemory<float> OverviewEmbedding { get; set; }
}