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
        var queryEmbedding = new float[1536];

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
        var vector = new float[1536];
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
        var vectorA = new float[1536];
        vectorA[0] = 1.0f;

        var cachedAnswer = "Luffy's crew is the Straw Hat Pirates.";
        var sources = new List<EpisodeRecord>();

        await service.SaveToCacheAsync(query, vectorA, cachedAnswer, sources);

        // Query with an orthogonal vector (similarity = 0.0)
        var vectorB = new float[1536];
        vectorB[1] = 1.0f;

        // Act
        var (isHit, answer, cachedSources, score) = await service.GetCachedResponseAsync(query, vectorB, 0.95);

        // Assert
        Assert.False(isHit);
        Assert.Null(answer);
        Assert.Null(cachedSources);
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
