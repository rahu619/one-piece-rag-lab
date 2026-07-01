using OnePieceApi.Retrieval;
using Xunit;

namespace one_piece_api.Tests;

public class InputFilterServiceTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("hi")]
    [InlineData("HEY")]
    [InlineData("yo!")]
    [InlineData("good morning...")]
    [InlineData(" hola ")]
    public void FilterInput_FiltersGreetings(string input)
    {
        // Arrange
        var service = new InputFilterService();

        // Act
        var (isFiltered, response) = service.FilterInput(input);

        // Assert
        Assert.True(isFiltered);
        Assert.Contains("Hello! I am your One Piece assistant", response);
    }

    [Theory]
    [InlineData("thanks")]
    [InlineData("thank you!")]
    [InlineData("cheers")]
    [InlineData("perfect")]
    [InlineData("awesome")]
    public void FilterInput_FiltersGratitude(string input)
    {
        // Arrange
        var service = new InputFilterService();

        // Act
        var (isFiltered, response) = service.FilterInput(input);

        // Assert
        Assert.True(isFiltered);
        Assert.Contains("You're welcome", response);
    }

    [Theory]
    [InlineData("who are you?")]
    [InlineData("what is your name")]
    [InlineData("what can you do?")]
    [InlineData("help")]
    public void FilterInput_FiltersBotInfoAndHelp(string input)
    {
        // Arrange
        var service = new InputFilterService();

        // Act
        var (isFiltered, response) = service.FilterInput(input);

        // Assert
        Assert.True(isFiltered);
        Assert.Contains("One Piece retrieval assistant", response);
    }

    [Theory]
    [InlineData("how are you")]
    [InlineData("hows it going?")]
    [InlineData("how's it going")]
    public void FilterInput_FiltersHowAreYou(string input)
    {
        // Arrange
        var service = new InputFilterService();

        // Act
        var (isFiltered, response) = service.FilterInput(input);

        // Assert
        Assert.True(isFiltered);
        Assert.Contains("doing great", response);
    }

    [Theory]
    [InlineData("Who is Zoro?")]
    [InlineData("What is the best episode in season 2?")]
    [InlineData("highest rated episode")]
    [InlineData("Tell me about Romance Dawn")]
    public void FilterInput_DoesNotFilterRAGQueries(string input)
    {
        // Arrange
        var service = new InputFilterService();

        // Act
        var (isFiltered, response) = service.FilterInput(input);

        // Assert
        Assert.False(isFiltered);
        Assert.Empty(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FilterInput_FiltersEmptyInput(string? input)
    {
        // Arrange
        var service = new InputFilterService();

        // Act
        var (isFiltered, response) = service.FilterInput(input);

        // Assert
        Assert.True(isFiltered);
        Assert.Contains("Please ask a question about One Piece", response);
    }
}
