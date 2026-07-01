using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;

namespace one_piece_api.Tests.Mocks;

/// <summary>
/// A lightweight in-memory mock for VectorStoreCollection of CacheRecord, using cosine similarity for search.
/// </summary>
public class MockCacheCollection : VectorStoreCollection<ulong, CacheRecord>
{
    private readonly List<CacheRecord> _store = new();

    public List<CacheRecord> Store => _store;

    public override string Name => "mock_cache";

    public MockCacheCollection()
    {
    }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    public override Task DeleteAsync(ulong key, CancellationToken cancellationToken = default)
    {
        _store.RemoveAll(r => r.Id == key);
        return Task.CompletedTask;
    }

    public override Task DeleteAsync(IEnumerable<ulong> keys, CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            _store.RemoveAll(r => r.Id == key);
        }
        return Task.CompletedTask;
    }

    public override Task<CacheRecord?> GetAsync(ulong key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        var record = _store.FirstOrDefault(r => r.Id == key);
        return Task.FromResult(record);
    }

    public override async IAsyncEnumerable<CacheRecord> GetAsync(
        IEnumerable<ulong> keys, 
        RecordRetrievalOptions? options = null, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            var record = _store.FirstOrDefault(r => r.Id == key);
            if (record != null)
            {
                yield return record;
            }
        }
    }

    public override async IAsyncEnumerable<CacheRecord> GetAsync(
        Expression<Func<CacheRecord, bool>> predicate,
        int limit,
        FilteredRecordRetrievalOptions<CacheRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var matched = _store.AsQueryable().Where(predicate).Take(limit);
        foreach (var record in matched)
        {
            yield return record;
        }
    }

    public override Task<ulong> UpsertAsync(CacheRecord record, CancellationToken cancellationToken = default)
    {
        _store.RemoveAll(r => r.Id == record.Id);
        _store.Add(record);
        return Task.FromResult(record.Id);
    }

    public override async Task<IEnumerable<ulong>> UpsertAsync(IEnumerable<CacheRecord> records, CancellationToken cancellationToken = default)
    {
        var keys = new List<ulong>();
        foreach (var record in records)
        {
            _store.RemoveAll(r => r.Id == record.Id);
            _store.Add(record);
            keys.Add(record.Id);
        }
        return keys;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public override async IAsyncEnumerable<VectorSearchResult<CacheRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<CacheRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (searchValue is not ReadOnlyMemory<float> queryVector)
        {
            yield break;
        }

        // Calculate cosine similarity for all records
        var scored = _store.Select(r =>
        {
            var similarity = ComputeCosineSimilarity(r.QueryEmbedding.Span, queryVector.Span);
            return new VectorSearchResult<CacheRecord>(r, similarity);
        })
        .OrderByDescending(r => r.Score)
        .Take(top);

        foreach (var result in scored)
        {
            yield return result;
        }
    }

    private static double ComputeCosineSimilarity(ReadOnlySpan<float> vecA, ReadOnlySpan<float> vecB)
    {
        if (vecA.Length != vecB.Length || vecA.Length == 0)
            return 0;

        double dotProduct = 0;
        double normA = 0;
        double normB = 0;

        for (int i = 0; i < vecA.Length; i++)
        {
            dotProduct += vecA[i] * vecB[i];
            normA += vecA[i] * vecA[i];
            normB += vecB[i] * vecB[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
