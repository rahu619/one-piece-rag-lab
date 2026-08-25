using System.Text.RegularExpressions;
using OnePieceApi.Config;

namespace OnePieceApi.Safety;

/// <summary>
/// Evaluates user queries before they reach any LLM prompt. Checks are deterministic, cheap,
/// and run in a fixed order; the first failing check determines the refusal reason.
/// A production system would layer a moderation model on top — see docs/ai-safety.md.
/// </summary>
public partial class InputGuardrails(SafetyOptions options)
{
    // Instruction-override and jailbreak phrasing. The middle wildcard is bounded so a benign
    // sentence such as "who ignored the marines" never matches: a match always ends on an
    // instruction-flavored noun.
    private static readonly (Regex Pattern, string Reason)[] InjectionPatterns =
    [
        (IgnoreInstructionsRegex(), GuardrailReasons.PromptInjection),
        (DisregardInstructionsRegex(), GuardrailReasons.PromptInjection),
        (ForgetInstructionsRegex(), GuardrailReasons.PromptInjection),
        (YouAreNowRegex(), GuardrailReasons.PromptInjection),
        (SystemPromptRegex(), GuardrailReasons.PromptInjection),
        (NewInstructionsRegex(), GuardrailReasons.PromptInjection),
        (RevealInstructionsRegex(), GuardrailReasons.PromptInjection),
        (JailbreakPersonaRegex(), GuardrailReasons.PromptInjection),
        (BypassSafetyRegex(), GuardrailReasons.PromptInjection),
        (NoRestrictionsRegex(), GuardrailReasons.PromptInjection),
        (OverrideRulesRegex(), GuardrailReasons.PromptInjection),
    ];

    // Phrase-based rather than keyword-based on purpose: the corpus is full of battles, so
    // single words like "kill" or "attack" would produce constant false positives.
    private static readonly (Regex Pattern, string Reason)[] HarmfulPatterns =
    [
        (SelfHarmRegex(), GuardrailReasons.SelfHarm),
        (WeaponSynthesisRegex(), GuardrailReasons.HarmfulRequest),
        (ViolenceIntentRegex(), GuardrailReasons.HarmfulRequest),
    ];

    public GuardrailReport Evaluate(string query)
    {
        if (!options.Enabled)
        {
            return GuardrailReport.AllowAll;
        }

        var decisions = new List<GuardrailDecision>(4);

        decisions.Add(query.Length <= options.MaxQueryLength
            ? GuardrailDecision.Pass(GuardrailCheck.Length)
            : GuardrailDecision.Fail(GuardrailCheck.Length,
                $"{GuardrailReasons.QueryTooLong} (max {options.MaxQueryLength} characters)"));

        if (options.BlockPromptInjection)
        {
            decisions.Add(FirstMatch(query, InjectionPatterns, GuardrailCheck.PromptInjection));
        }

        if (options.BlockPii)
        {
            decisions.Add(PiiRedactor.ContainsPii(query)
                ? GuardrailDecision.Fail(GuardrailCheck.PersonalData, GuardrailReasons.PersonalData)
                : GuardrailDecision.Pass(GuardrailCheck.PersonalData));
        }

        if (options.BlockHarmfulContent)
        {
            decisions.Add(FirstMatch(query, HarmfulPatterns, GuardrailCheck.HarmfulContent));
        }

        var firstFailure = decisions.FirstOrDefault(d => !d.Passed);
        return new GuardrailReport(firstFailure is null, decisions);
    }

    private static GuardrailDecision FirstMatch(
        string text,
        (Regex Pattern, string Reason)[] patterns,
        GuardrailCheck check)
    {
        foreach (var (pattern, reason) in patterns)
        {
            if (pattern.IsMatch(text))
            {
                return GuardrailDecision.Fail(check, reason);
            }
        }

        return GuardrailDecision.Pass(check);
    }

    /// <summary>
    /// Maps a failing decision to the message shown to the user. Self-harm content gets a
    /// supportive response with a pointer to professional help rather than a plain refusal.
    /// </summary>
    public static string GetRefusalMessage(GuardrailDecision failure) => failure.Reason switch
    {
        var r when r?.StartsWith(GuardrailReasons.QueryTooLong, StringComparison.Ordinal) == true =>
            "That message is too long for me to process. Please keep queries under the configured character limit.",

        GuardrailReasons.PersonalData =>
            "Please don't share personal information such as email addresses, phone numbers, or IDs. " +
            "Re-ask your One Piece question without it and I'll be happy to help.",

        GuardrailReasons.SelfHarm =>
            "I'm only a One Piece episode assistant, but what you're asking about matters. " +
            "If you're struggling, please reach out to someone — findahelpline.com lists free, " +
            "confidential support lines in many countries.",

        GuardrailReasons.HarmfulRequest =>
            "I can't help with requests like that. Ask me about One Piece episodes, characters, or ratings instead.",

        _ =>
            "I can't process requests that try to override my instructions. " +
            "Ask me about One Piece episodes, characters, or ratings instead.",
    };

    [GeneratedRegex(@"\bignore\b[^.!?\n]{0,60}\b(instructions?|prompts?|rules?|guidelines?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoreInstructionsRegex();

    [GeneratedRegex(@"\bdisregard\b[^.!?\n]{0,60}\b(instructions?|prompts?|rules?|guidelines?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DisregardInstructionsRegex();

    [GeneratedRegex(@"\bforget\b[^.!?\n]{0,60}\b(instructions?|rules?|programming|training)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForgetInstructionsRegex();

    [GeneratedRegex(@"\byou are now\b", RegexOptions.IgnoreCase)]
    private static partial Regex YouAreNowRegex();

    [GeneratedRegex(@"\bsystem prompt\b", RegexOptions.IgnoreCase)]
    private static partial Regex SystemPromptRegex();

    [GeneratedRegex(@"\bnew instructions?\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewInstructionsRegex();

    [GeneratedRegex(@"\breveal\b[^.!?\n]{0,40}\b(instructions?|prompts?|system)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RevealInstructionsRegex();

    [GeneratedRegex(@"\b(do anything now|DAN mode|jailbreak)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JailbreakPersonaRegex();

    [GeneratedRegex(@"\bbypass\b[^.!?\n]{0,30}\b(safety|security|filters?|restrictions?|guardrails?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BypassSafetyRegex();

    [GeneratedRegex(@"\b(pretend|act|behave)\b[^.!?\n]{0,40}\bno (restrictions?|rules?|limits?|filters?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoRestrictionsRegex();

    [GeneratedRegex(@"\boverride\b[^.!?\n]{0,30}\b(safety|guidelines?|programming|instructions?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverrideRulesRegex();

    [GeneratedRegex(@"\b(suicide|kill myself|end my life|self[- ]?harm|hurt myself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelfHarmRegex();

    [GeneratedRegex(
        @"\b(make|build|create|assemble|synthesize|manufacture)\b[^.!?\n]{0,50}\b(bombs?|explosives?|grenades?|napalm|chemical weapons?|bioweapons?|firearms?|methamphetamine|fentanyl|heroin|poisons?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WeaponSynthesisRegex();

    [GeneratedRegex(
        @"\b(how (do i|to|can i)|help me|teach me)\b[^.!?\n]{0,40}\b(kill|murder|hurt|harm|attack)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ViolenceIntentRegex();
}
