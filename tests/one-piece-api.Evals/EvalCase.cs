using System.Text.Json;
using OnePieceApi.Pipeline;

namespace OnePieceApi.Evals;

/// <summary>
/// One golden evaluation case. Depending on the suite, the scorer asserts on ExpectedRoute,
/// ExpectBlocked, and/or the fact substrings.
/// </summary>
public sealed record EvalCase
{
    public required string Id { get; init; }

    public required string Query { get; init; }

    /// <summary>"SQL", "VECTOR", or "GENERAL".</summary>
    public string? ExpectedRoute { get; init; }

    public bool ExpectBlocked { get; init; }

    /// <summary>At least one of these substrings must appear in the answer (case-insensitive).</summary>
    public List<string>? FactsAny { get; init; }

    /// <summary>Every one of these substrings must appear in the answer (case-insensitive).</summary>
    public List<string>? FactsAll { get; init; }

    public static async Task<List<EvalCase>> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return JsonSerializer.Deserialize<List<EvalCase>>(json, CaseSerializerOptions) ?? [];
    }

    public static readonly JsonSerializerOptions CaseSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// One assertion applied to a case result.
/// </summary>
public sealed record CaseCheck(string Name, bool Passed, string Detail);

/// <summary>
/// The full outcome of running one eval case through the pipeline.
/// </summary>
public sealed record CaseResult
{
    public required EvalCase Case { get; init; }
    public required string Suite { get; init; }
    public required QueryOutcome Outcome { get; init; }
    public required RouteKind Route { get; init; }
    public required string Answer { get; init; }
    public required string TraceId { get; init; }
    public required IReadOnlyList<CaseCheck> Checks { get; init; }

    /// <summary>Retrieved context kept around for the optional groundedness judge.</summary>
    public IReadOnlyList<OnePieceApi.Models.EpisodeRecord> Sources { get; init; } = [];

    public string? SqlResults { get; init; }

    public bool? Grounded { get; init; }

    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Passed);
}

/// <summary>
/// Aggregate pass rate for one suite, compared against the baselines.
/// </summary>
public sealed record SuiteSummary(string Suite, int Total, int Passed)
{
    public double Rate => Total == 0 ? 1.0 : (double)Passed / Total;
}

public sealed record EvalBaselines
{
    public double RoutingAccuracy { get; init; }
    public double QaPassRate { get; init; }
    public double SafetyBlockRate { get; init; }
    public double SafetyFalsePositiveMax { get; init; }
}
