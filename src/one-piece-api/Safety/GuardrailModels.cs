namespace OnePieceApi.Safety;

/// <summary>
/// The guardrail checks a query or an answer passes through.
/// </summary>
public enum GuardrailCheck
{
    Length,
    PromptInjection,
    PersonalData,
    HarmfulContent,
}

/// <summary>
/// The outcome of one guardrail check.
/// </summary>
/// <param name="Check">Which guardrail ran.</param>
/// <param name="Passed">True when the content is safe to proceed with.</param>
/// <param name="Reason">Machine-stable failure reason; null when passed.</param>
public sealed record GuardrailDecision(GuardrailCheck Check, bool Passed, string? Reason)
{
    public static GuardrailDecision Pass(GuardrailCheck check) => new(check, true, null);

    public static GuardrailDecision Fail(GuardrailCheck check, string reason) => new(check, false, reason);
}

/// <summary>
/// Aggregated verdict for every check that ran, in evaluation order.
/// </summary>
public sealed record GuardrailReport(bool Allowed, IReadOnlyList<GuardrailDecision> Decisions)
{
    public GuardrailDecision? FirstFailure =>
        Decisions.Count > 0 ? Decisions.FirstOrDefault(d => !d.Passed) : null;

    public static readonly GuardrailReport AllowAll = new(true, []);
}

/// <summary>
/// Stable failure reasons shared by input and output guardrails. The query engine maps these
/// to user-facing refusal messages, and evaluation datasets assert on them.
/// </summary>
public static class GuardrailReasons
{
    public const string QueryTooLong = "Query exceeds the maximum length";
    public const string PromptInjection = "Prompt injection attempt detected";
    public const string PersonalData = "Personal information detected";
    public const string SelfHarm = "Self-harm content";
    public const string HarmfulRequest = "Harmful content request";
    public const string HarmfulOutput = "Harmful content in model output";
}
