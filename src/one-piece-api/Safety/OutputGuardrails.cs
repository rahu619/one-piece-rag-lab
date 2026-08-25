using System.Text.RegularExpressions;
using OnePieceApi.Config;

namespace OnePieceApi.Safety;

/// <summary>
/// The result of reviewing a model answer: the verdict plus the text that is safe to show
/// (identical to the input unless personal data had to be redacted).
/// </summary>
public sealed record OutputReview(GuardrailReport Report, string SafeAnswer);

/// <summary>
/// Reviews model answers before they reach the user or the semantic cache. Harmful content
/// fails the review outright; personal data is redacted and the answer still passes.
/// </summary>
public partial class OutputGuardrails(SafetyOptions options)
{
    [GeneratedRegex(
        @"\b(make|build|create|assemble|synthesize|manufacture)\b[^.!?\n]{0,50}\b(bombs?|explosives?|grenades?|napalm|chemical weapons?|bioweapons?|firearms?|methamphetamine|fentanyl|heroin|poisons?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WeaponSynthesisRegex();

    [GeneratedRegex(@"\b(suicide|kill myself|end my life|self[- ]?harm|hurt myself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelfHarmRegex();

    public OutputReview Review(string answer)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(answer))
        {
            return new OutputReview(GuardrailReport.AllowAll, answer);
        }

        var decisions = new List<GuardrailDecision>(2);

        if (options.BlockHarmfulContent)
        {
            var harmful = WeaponSynthesisRegex().IsMatch(answer) || SelfHarmRegex().IsMatch(answer);
            decisions.Add(harmful
                ? GuardrailDecision.Fail(GuardrailCheck.HarmfulContent, GuardrailReasons.HarmfulOutput)
                : GuardrailDecision.Pass(GuardrailCheck.HarmfulContent));
        }

        var firstFailure = decisions.FirstOrDefault(d => !d.Passed);
        if (firstFailure is not null)
        {
            return new OutputReview(new GuardrailReport(false, decisions), answer);
        }

        var safeAnswer = answer;
        if (options.BlockPii && PiiRedactor.ContainsPii(answer))
        {
            safeAnswer = PiiRedactor.Redact(answer);
            decisions.Add(GuardrailDecision.Pass(GuardrailCheck.PersonalData));
        }

        return new OutputReview(new GuardrailReport(true, decisions), safeAnswer);
    }

    public static string GetRefusalMessage(GuardrailDecision failure) =>
        "I can't share that kind of content. Ask me about One Piece episodes, characters, or ratings instead.";
}
