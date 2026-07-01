using System;
using System.Threading.Tasks;
using one_piece_api.Tests.Mocks;
using OnePieceApi.Config;
using OnePieceApi.Retrieval;
using Xunit;

namespace one_piece_api.Tests;

public class InputFilterServiceTests
{
    private readonly MockChatClient _chatClient = new();

    [Theory]
    [InlineData("hello")]
    [InlineData("hi")]
    [InlineData("HEY")]
    [InlineData("yo")]
    [InlineData("good morning")]
    [InlineData("how are you")]
    [InlineData("who are you")]
    [InlineData("what is the capital of France?")]
    [InlineData("how do I write a binary search in Python?")]
    public async Task ClassifyQueryAsync_RoutesGeneralAndOutofDomainQueries_AsGeneral(string input)
    {
        // Arrange
        var service = new InputFilterService(_chatClient, new InputFilterOptions());

        // Act
        var intent = await service.ClassifyQueryAsync(input);

        // Assert
        Assert.Equal(QueryIntent.General, intent);
    }

    [Theory]
    [InlineData("Who is Zoro?")]
    [InlineData("What is the best episode in season 2?")]
    [InlineData("highest rated episode")]
    [InlineData("Tell me about Romance Dawn")]
    [InlineData("Luffy's crew members")]
    public async Task ClassifyQueryAsync_RoutesOnePieceQueries_AsOnePiece(string input)
    {
        // Arrange
        var service = new InputFilterService(_chatClient, new InputFilterOptions());

        // Act
        var intent = await service.ClassifyQueryAsync(input);

        // Assert
        Assert.Equal(QueryIntent.OnePiece, intent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ClassifyQueryAsync_RoutesEmptyInput_AsGeneral(string? input)
    {
        // Arrange
        var service = new InputFilterService(_chatClient, new InputFilterOptions());

        // Act
        var intent = await service.ClassifyQueryAsync(input!);

        // Assert
        Assert.Equal(QueryIntent.General, intent);
    }
}
