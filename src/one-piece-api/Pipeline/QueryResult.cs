using OnePieceApi.Models;
using OnePieceApi.Safety;

namespace OnePieceApi.Pipeline;

/// <summary>
/// Terminal state of a query.
/// </summary>
public enum QueryOutcome
{
    /// <summary>An answer was produced (fresh or from cache).</summary>
    Answered,

    /// <summary>An input guardrail rejected the query before any LLM call.</summary>
    Blocked,

    /// <summary>An output guardrail withheld the generated answer.</summary>
    Refused,

    /// <summary>The VECTOR route found no matching episodes.</summary>
    NoMatches,
}

/// <summary>
/// Which execution path served the query.
/// </summary>
public enum RouteKind
{
    None,
    General,
    Sql,
    Vector,
}

/// <summary>
/// Interaction with the semantic cache for this query.
/// </summary>
public enum CacheOutcome
{
    Disabled,
    ExactHit,
    SemanticHit,
    Miss,
    Saved,
}

/// <summary>
/// Progress events the engine raises so a UI can render each stage in order while the
/// answer is still streaming.
/// </summary>
public enum PipelineStage
{
    Routing,
    SemanticCacheMiss,
    RouteSql,
    RouteVector,
    RouteGeneral,
    SqlGenerated,
    AnswerStart,
}

/// <summary>
/// A stage event plus optional detail (e.g. the generated SQL text).
/// </summary>
public sealed record PipelineEvent(PipelineStage Stage, string? Detail = null);

/// <summary>
/// Everything a caller needs to render and audit one query execution.
/// </summary>
public sealed record QueryResult
{
    public required string Query { get; init; }

    public required QueryOutcome Outcome { get; init; }

    public RouteKind Route { get; init; }

    /// <summary>The answer text, or the refusal message when blocked/refused.</summary>
    public string Answer { get; init; } = string.Empty;

    public IReadOnlyList<EpisodeRecord> Sources { get; init; } = [];

    public CacheOutcome Cache { get; init; }

    public double? CacheSimilarity { get; init; }

    public string? GeneratedSql { get; init; }

    public string? SqlResults { get; init; }

    public IReadOnlyList<GuardrailDecision> GuardrailDecisions { get; init; } = [];

    public string TraceId { get; init; } = string.Empty;

    public TimeSpan Elapsed { get; init; }
}
