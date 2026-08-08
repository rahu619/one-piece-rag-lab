using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using one_piece_api.Tests.Mocks;
using OnePieceApi.Models;
using OnePieceApi.Retrieval;
using Xunit;

namespace one_piece_api.Tests;

public class SemanticCacheServiceTests
{
    [Fact]
    public async Task GetCachedResponseAsync_ReturnsFalse_WhenCacheIsEmpty()
    {
        // Arrange
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);
        var query = "Who is Luffy's crew?";
        var queryEmbedding = new float[EmbeddingSchema.Dimensions];

        // Act
        var (isHit, answer, sources, score) = await service.GetCachedResponseAsync(query, queryEmbedding, 0.95);

        // Assert
        Assert.False(isHit);
        Assert.Null(answer);
        Assert.Null(sources);
        Assert.Null(score);
    }

    [Fact]
    public async Task SaveToCacheAsync_And_GetCachedResponseAsync_ReturnsHit_WhenQueryIsSimilar()
    {
        // Arrange
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);

        var query = "Who is Luffy's crew?";
        var vector = new float[EmbeddingSchema.Dimensions];
        vector[0] = 1.0f;

        var cachedAnswer = "Luffy's crew is the Straw Hat Pirates.";
        var sources = new List<EpisodeRecord>
        {
            new() { Id = 1, Title = "Romance Dawn", Rating = 8.5f }
        };

        // Act
        await service.SaveToCacheAsync(query, vector, cachedAnswer, sources);

        // Try exact query
        var (isHit, answer, cachedSources, score) = await service.GetCachedResponseAsync(query, vector, 0.95);

        // Assert
        Assert.True(isHit);
        Assert.Equal(cachedAnswer, answer);
        Assert.NotNull(cachedSources);
        Assert.Single(cachedSources);
        Assert.Equal("Romance Dawn", cachedSources[0].Title);
        Assert.NotNull(score);
        Assert.Equal(1.0, score.Value, precision: 4);
    }

    [Fact]
    public async Task GetCachedResponseAsync_ReturnsMiss_WhenSimilarityIsBelowThreshold()
    {
        // Arrange
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);

        var query = "Who is Luffy's crew?";
        var vectorA = new float[EmbeddingSchema.Dimensions];
        vectorA[0] = 1.0f;

        var cachedAnswer = "Luffy's crew is the Straw Hat Pirates.";
        var sources = new List<EpisodeRecord>();

        await service.SaveToCacheAsync(query, vectorA, cachedAnswer, sources);

        // Query with an orthogonal vector (similarity = 0.0)
        var vectorB = new float[EmbeddingSchema.Dimensions];
        vectorB[1] = 1.0f;

        // Act
        var (isHit, answer, cachedSources, score) = await service.GetCachedResponseAsync(query, vectorB, 0.95);

        // Assert
        Assert.False(isHit);
        Assert.Null(answer);
        Assert.Null(cachedSources);
    }

    [Fact]
    public async Task GetExactMatchAsync_ReturnsHit_WithoutNeedingAnEmbedding()
    {
        // Arrange
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);

        var query = "Who is Luffy's crew?";
        var vector = new float[EmbeddingSchema.Dimensions];
        vector[0] = 1.0f;

        await service.SaveToCacheAsync(query, vector, "Luffy's crew is the Straw Hat Pirates.", [
            new EpisodeRecord { Id = 1, Title = "Romance Dawn", Rating = 8.5f }
        ]);

        // Act
        var (isHit, answer, sources) = await service.GetExactMatchAsync(query);

        // Assert
        Assert.True(isHit);
        Assert.Equal("Luffy's crew is the Straw Hat Pirates.", answer);
        Assert.NotNull(sources);
        Assert.Equal("Romance Dawn", sources[0].Title);
    }

    [Fact]
    public async Task GetExactMatchAsync_ReturnsMiss_ForADifferentQuery()
    {
        // Arrange
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);

        await service.SaveToCacheAsync("Who is Luffy's crew?", new float[EmbeddingSchema.Dimensions], "An answer.", []);

        // Act
        var (isHit, answer, sources) = await service.GetExactMatchAsync("Who is Zoro?");

        // Assert
        Assert.False(isHit);
        Assert.Null(answer);
        Assert.Null(sources);
    }

    [Fact]
    public async Task GetExactMatchAsync_ReturnsMiss_WhenTheStoredTextDoesNotMatchTheKey()
    {
        // A hash key can collide, so the stored query text has to be confirmed before it is trusted.
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);

        var query = "Who is Luffy's crew?";
        await mockCollection.UpsertAsync(new CacheRecord
        {
            Id = SemanticCacheService.GetFnv1aHash(query),
            Query = "a different query that happened to land on this key",
            Answer = "Wrong answer.",
            QueryEmbedding = new float[EmbeddingSchema.Dimensions]
        });

        // Act
        var (isHit, answer, _) = await service.GetExactMatchAsync(query);

        // Assert
        Assert.False(isHit);
        Assert.Null(answer);
    }

    [Fact]
    public async Task ClearAsync_EmptiesTheCache()
    {
        // Arrange
        var mockCollection = new MockCacheCollection();
        var service = new SemanticCacheService(mockCollection);

        await service.SaveToCacheAsync("Who is Luffy's crew?", new float[EmbeddingSchema.Dimensions], "An answer.", []);
        Assert.Single(mockCollection.Store);

        // Act
        await service.ClearAsync();

        // Assert
        Assert.Empty(mockCollection.Store);
    }

    [Fact]
    public void GetFnv1aHash_IsDeterministicAndConsistent()
    {
        // Arrange & Act
        var hash1 = SemanticCacheService.GetFnv1aHash("Who is Luffy's crew?");
        var hash2 = SemanticCacheService.GetFnv1aHash("Who is Luffy's crew?");
        var hash3 = SemanticCacheService.GetFnv1aHash("Different query text");

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
        Assert.NotEqual(0UL, hash1);
    }
}
