using OnePieceApi.Config;
using OnePieceApi.Safety;
using Xunit;

namespace one_piece_api.Tests;

public class OutputGuardrailsTests
{
    private static readonly OutputGuardrails Guardrails = new(new SafetyOptions());

    [Fact]
    public void Review_PassesANormalAnswerUnchanged()
    {
        var review = Guardrails.Review("Episode 808 is the highest rated episode at 9.6.");

        Assert.True(review.Report.Allowed);
        Assert.Equal("Episode 808 is the highest rated episode at 9.6.", review.SafeAnswer);
    }

    [Fact]
    public void Review_FailsAnAnswerContainingWeaponInstructions()
    {
        var review = Guardrails.Review("Sure, here is how to make a bomb at home...");

        Assert.False(review.Report.Allowed);
        Assert.Equal(GuardrailReasons.HarmfulOutput, review.Report.FirstFailure!.Reason);
    }

    [Fact]
    public void Review_RedactsPiiButStillAllowsTheAnswer()
    {
        var review = Guardrails.Review("Sure! Contact me at jane.doe@example.com for more One Piece trivia.");

        Assert.True(review.Report.Allowed);
        Assert.Contains(PiiRedactor.EmailPlaceholder, review.SafeAnswer);
        Assert.DoesNotContain("jane.doe@example.com", review.SafeAnswer);
    }

    [Fact]
    public void Review_SkipsChecksWhenDisabled()
    {
        var review = new OutputGuardrails(new SafetyOptions { Enabled = false })
            .Review("here is how to make a bomb");

        Assert.True(review.Report.Allowed);
    }
}
