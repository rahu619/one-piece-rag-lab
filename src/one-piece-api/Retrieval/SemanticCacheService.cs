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
    /// Looks up a byte-identical repeat of a previous query by its deterministic key.
    /// This is a direct key fetch, so a repeated query costs neither an embedding round trip
    /// nor a vector search.
    /// </summary>
    /// <param name="query">The text query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating if it is a hit, the cached answer, and cached sources.</returns>
    public async Task<(bool IsHit, string? Answer, List<EpisodeRecord>? Sources)> GetExactMatchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var record = await cacheCollection.GetAsync(GetFnv1aHash(query), cancellationToken: cancellationToken);

        // The key is a hash, so confirm the stored text before trusting the entry.
        if (record is null || !string.Equals(record.Query, query, StringComparison.Ordinal))
        {
            return (false, null, null);
        }

        return (true, record.Answer, DeserializeSources(record.SourcesJson));
    }

    /// <summary>
    /// Removes every cached entry. Called when the underlying dataset is rebuilt, otherwise
    /// cached answers keep citing episodes that no longer exist.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await cacheCollection.EnsureCollectionDeletedAsync(cancellationToken);
        await cacheCollection.EnsureCollectionExistsAsync(cancellationToken);
    }

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
                return (true, cachedRecord.Answer, DeserializeSources(cachedRecord.SourcesJson), item.Score);
            }
        }

        return (false, null, null, null);
    }

    private static List<EpisodeRecord>? DeserializeSources(string sourcesJson)
    {
        if (string.IsNullOrEmpty(sourcesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<EpisodeRecord>>(sourcesJson);
        }
        catch (JsonException)
        {
            // Fallback in case of deserialization errors
            return null;
        }
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
