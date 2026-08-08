using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Service for searching episodes based on a precomputed query embedding.
/// </summary>
/// <param name="collection"></param>
public class SearchService(VectorStoreCollection<ulong, EpisodeRecord> collection)
{
    /// <summary>
    /// Searches for episodes based on the precomputed query embedding.
    /// </summary>
    public async Task<List<EpisodeRecord>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int limit = 5, // TopK 
        CancellationToken cancellationToken = default)
    {
        var searchOptions = new VectorSearchOptions<EpisodeRecord>();

        var results = collection.SearchAsync(
            queryVector,
            limit,
            searchOptions,
            cancellationToken);

        var records = new List<EpisodeRecord>();

        await foreach (var item in results.WithCancellation(cancellationToken))
        {
            records.Add(item.Record);
        }

        return records;
    }
}