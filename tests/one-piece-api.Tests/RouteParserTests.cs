using OnePieceApi.Pipeline;
using Xunit;

namespace one_piece_api.Tests;

public class RouteParserTests
{
    [Theory]
    [InlineData("SQL", "SQL")]
    [InlineData("VECTOR", "VECTOR")]
    [InlineData("GENERAL", "GENERAL")]
    [InlineData("sql", "SQL")]
    [InlineData("\"VECTOR\"", "VECTOR")]
    [InlineData("`SQL`", "SQL")]
    [InlineData("SQL.", "SQL")]
    [InlineData("The answer is SQL", "GENERAL")] // only the leading token counts
    [InlineData("VECTOR and SQL both fit", "VECTOR")]
    [InlineData("hello there", "GENERAL")]
    [InlineData("", "GENERAL")]
    [InlineData(null, "GENERAL")]
    public void ParseIntent_MapsTheLeadingTokenToARoute(string? response, string expected)
    {
        Assert.Equal(expected, RouteParser.ParseIntent(response));
    }

    [Fact]
    public void ParseIntent_IgnoresPromptEchoesByOnlyReadingTheFirstToken()
    {
        // Small models sometimes echo the prompt, which names every category. A Contains-based
        // match would land on whichever category appears anywhere in the echo, so only the
        // leading token counts: here "SQL" appears later, but VECTOR is the leading token.
        var echoed = "VECTOR. SQL would also fit this query.";

        Assert.Equal("VECTOR", RouteParser.ParseIntent(echoed));
    }

    [Theory]
    [InlineData("SELECT * FROM Episodes", "SELECT * FROM Episodes")]
    [InlineData("```sql\nSELECT 1\n```", "SELECT 1")]
    [InlineData("```\nSELECT 1\n```", "SELECT 1")]
    [InlineData("  SELECT 1;  ", "SELECT 1;")]
    public void StripCodeFence_RemovesMarkdownFencesAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, RouteParser.StripCodeFence(input));
    }
}
