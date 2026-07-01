using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Service for managing semantic cache in Qdrant, matching incoming queries with previous ones.
/// </summary>
public class SemanticCacheService(VectorStoreCollection<ulong, CacheRecord> cacheCollection)
{
    /// <summary>
    /// Checks the semantic cache for a query semantically similar to the provided embedding.
    /// </summary>
    /// <param name="query">The text query.</param>
    /// <param name="queryEmbedding">The embedding of the query.</param>
    /// <param name="similarityThreshold">The minimum similarity score required for a hit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating if it is a hit, the cached answer, cached sources, and the similarity score.</returns>
    public async Task<(bool IsHit, string? Answer, List<EpisodeRecord>? Sources, double? Score)> GetCachedResponseAsync(
        string query,
        ReadOnlyMemory<float> queryEmbedding,
        double similarityThreshold,
        CancellationToken cancellationToken = default)
    {
        var searchOptions = new VectorSearchOptions<CacheRecord>();
        var results = cacheCollection.SearchAsync(
            queryEmbedding,
            top: 1,
            searchOptions,
            cancellationToken);

        await foreach (var item in results.WithCancellation(cancellationToken))
        {
            if (item.Score >= similarityThreshold)
            {
                var cachedRecord = item.Record;
                List<EpisodeRecord>? sources = null;

                if (!string.IsNullOrEmpty(cachedRecord.SourcesJson))
                {
                    try
                    {
                        sources = JsonSerializer.Deserialize<List<EpisodeRecord>>(cachedRecord.SourcesJson);
                    }
                    catch
                    {
                        // Fallback in case of deserialization errors
                    }
                }

                return (true, cachedRecord.Answer, sources, item.Score);
            }
        }

        return (false, null, null, null);
    }

    /// <summary>
    /// Saves a query, its embedding, the generated answer, and the retrieved sources to the semantic cache.
    /// </summary>
    public async Task SaveToCacheAsync(
        string query,
        ReadOnlyMemory<float> queryEmbedding,
        string answer,
        List<EpisodeRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var id = GetFnv1aHash(query);

        // Map EpisodeRecords to exclude any embeddings to minimize cache size and JSON size.
        var serializedSources = JsonSerializer.Serialize(sources.Select(s => new EpisodeRecord
        {
            Id = s.Id,
            Title = s.Title,
            Overview = s.Overview,
            Season = s.Season,
            EpisodeNumber = s.EpisodeNumber,
            ReleaseYear = s.ReleaseYear,
            Rating = s.Rating
            // OverviewEmbedding is left unassigned
        }));

        var record = new CacheRecord
        {
            Id = id,
            Query = query,
            Answer = answer,
            SourcesJson = serializedSources,
            QueryEmbedding = queryEmbedding
        };

        await cacheCollection.UpsertAsync(record, cancellationToken);
    }

    /// <summary>
    /// Generates a deterministic ulong hash for the query using the FNV-1a algorithm.
    /// </summary>
    public static ulong GetFnv1aHash(string text)
    {
        if (text == null) return 0;
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
