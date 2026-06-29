using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Service for searching episodes based on a query string using embeddings.
/// </summary>
/// <param name="embeddingGenerator"></param>
/// <param name="collection"></param>
public class SearchService(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStoreCollection<ulong, EpisodeRecord> collection)
{
    /// <summary>
    /// Searches for episodes based on the provided query string, returning a list of matching EpisodeRecord objects.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="limit"></param>
    /// <param name="embeddingGenerationOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<EpisodeRecord>> SearchAsync(string query, int limit = 5,
    EmbeddingGenerationOptions? embeddingGenerationOptions = default,
    CancellationToken cancellationToken = default)
    {
        var options = embeddingGenerationOptions ?? new EmbeddingGenerationOptions
        {
            ModelId = "text-embedding-3-large"
        };

        var embedding = await embeddingGenerator.GenerateAsync(
            query, 
            options, 
            cancellationToken);

        // var options = new VectorSearchOptions<EpisodeRecord>
        // {

        // };

        var results = new List<EpisodeRecord>();

        await foreach (var result in collection.SearchAsync(
            embedding.Vector, // Use the generated embedding for the query
            limit,
            default,
            cancellationToken))
        {
            if (result.Record is not null)
            {
                results.Add(result.Record);
            }
        }

        return results;
    }
}