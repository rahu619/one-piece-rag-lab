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
    private const string DefaultEmbeddingModelId = "qwen2.5-coder:1.5b";

    private static readonly EmbeddingGenerationOptions DefaultEmbeddingOptions =
    new()
    {
        ModelId = DefaultEmbeddingModelId
    };
    /// <summary>
    /// Searches for episodes based on the provided query string, returning a list of matching EpisodeRecord objects.
    /// </summary>
    /// <param name="query"></param>
    /// <param name="limit"></param>
    /// <param name="embeddingGenerationOptions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<EpisodeRecord>> SearchAsync(
    string query,
    int limit = 20, // TopK 
    EmbeddingGenerationOptions? embeddingGenerationOptions = default,
    CancellationToken cancellationToken = default)
{
    var options = embeddingGenerationOptions ?? DefaultEmbeddingOptions;

    var queryEmbedding = await embeddingGenerator.GenerateAsync(
        query,
        options,
        cancellationToken);

    var searchOptions = new VectorSearchOptions<EpisodeRecord>();

    var results = collection.SearchAsync(
        queryEmbedding.Vector,
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