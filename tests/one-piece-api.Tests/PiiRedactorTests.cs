using OnePieceApi.Safety;
using Xunit;

namespace one_piece_api.Tests;

public class PiiRedactorTests
{
    [Theory]
    [InlineData("reach me at jane.doe@example.com please", true)]
    [InlineData("call (555) 123-4567", true)]
    [InlineData("call me at +1 555-123-4567", true)]
    [InlineData("ssn 123-45-6789", true)]
    [InlineData("card 4111 1111 1111 1111", true)]
    [InlineData("what is the highest rated episode?", false)]
    [InlineData("episode 808 aired in 2017 with rating 9.6", false)]
    [InlineData("958 episodes across 22 years", false)]
    public void ContainsPii_MatchesOnlyPersonalDataShapes(string text, bool expected)
    {
        Assert.Equal(expected, PiiRedactor.ContainsPii(text));
    }

    [Fact]
    public void Redact_ReplacesEachShapeWithATypedPlaceholder()
    {
        var text = "Email jane.doe@example.com or call (555) 123-4567, SSN 123-45-6789, card 4111111111111111";

        var redacted = PiiRedactor.Redact(text);

        Assert.Contains(PiiRedactor.EmailPlaceholder, redacted);
        Assert.Contains(PiiRedactor.PhonePlaceholder, redacted);
        Assert.Contains(PiiRedactor.NationalIdPlaceholder, redacted);
        Assert.Contains(PiiRedactor.CardNumberPlaceholder, redacted);
        Assert.DoesNotContain("jane.doe", redacted);
        Assert.DoesNotContain("123-4567", redacted);
        Assert.DoesNotContain("123-45-6789", redacted);
        Assert.DoesNotContain("4111111111111111", redacted);
    }

    [Fact]
    public void Redact_LeavesEpisodeDataUntouched()
    {
        var text = "Episode 808 (Season 1, 2017) has a rating of 9.6 across 958 rows.";

        Assert.Equal(text, PiiRedactor.Redact(text));
    }
}
