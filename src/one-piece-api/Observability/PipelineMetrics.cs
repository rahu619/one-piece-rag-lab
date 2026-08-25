using System.Text;
using Microsoft.Extensions.AI;

namespace OnePieceApi.Observability;

/// <summary>
/// Immutable view over the accumulated counters, safe to render or serialize while the
/// pipeline keeps running.
/// </summary>
public sealed record MetricsSnapshot
{
    public required int Queries { get; init; }
    public required int Answered { get; init; }
    public required int Blocked { get; init; }
    public required int Refused { get; init; }
    public required int NoMatches { get; init; }
    public required IReadOnlyDictionary<string, int> Routes { get; init; }
    public required int ExactCacheHits { get; init; }
    public required int SemanticCacheHits { get; init; }
    public required int CacheMisses { get; init; }
    public required IReadOnlyDictionary<string, int> GuardrailBlocks { get; init; }
    public required int LlmCalls { get; init; }
    public required int LlmErrors { get; init; }
    public required int EmbeddingCalls { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required double AverageQueryLatencyMs { get; init; }
}

/// <summary>
/// In-process counters for the query pipeline: outcomes, routing distribution, cache
/// effectiveness, guardrail activity, and LLM cost/latency. Rendered by the interactive
/// "/stats" command and snapshotted by the eval harness.
/// </summary>
public class PipelineMetrics
{
    private const int RecentLatencyCap = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _guardrailBlocks = new(StringComparer.Ordinal);
    private readonly Queue<double> _recentQueryLatencies = new();

    private int _queries;
    private int _answered;
    private int _blocked;
    private int _refused;
    private int _noMatches;
    private int _exactCacheHits;
    private int _semanticCacheHits;
    private int _cacheMisses;
    private int _llmCalls;
    private int _llmErrors;
    private int _embeddingCalls;
    private long _inputTokens;
    private long _outputTokens;
    private double _queryLatencyMsTotal;

    public void RecordQueryOutcome(string outcome, TimeSpan elapsed)
    {
        lock (_gate)
        {
            _queries++;
            _queryLatencyMsTotal += elapsed.TotalMilliseconds;

            _recentQueryLatencies.Enqueue(elapsed.TotalMilliseconds);
            while (_recentQueryLatencies.Count > RecentLatencyCap)
            {
                _recentQueryLatencies.Dequeue();
            }

            switch (outcome)
            {
                case "Answered": _answered++; break;
                case "Blocked": _blocked++; break;
                case "Refused": _refused++; break;
                case "NoMatches": _noMatches++; break;
            }
        }
    }

    public void RecordRoute(string route)
    {
        lock (_gate)
        {
            _routes.TryGetValue(route, out var count);
            _routes[route] = count + 1;
        }
    }

    public void RecordCache(string kind)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case "ExactHit": _exactCacheHits++; break;
                case "SemanticHit": _semanticCacheHits++; break;
                case "Miss": _cacheMisses++; break;
            }
        }
    }

    public void RecordGuardrailBlock(string reason)
    {
        lock (_gate)
        {
            _guardrailBlocks.TryGetValue(reason, out var count);
            _guardrailBlocks[reason] = count + 1;
        }
    }

    public void RecordLlmCall(TimeSpan elapsed, UsageDetails? usage)
    {
        lock (_gate)
        {
            _llmCalls++;
            _inputTokens += usage?.InputTokenCount ?? 0;
            _outputTokens += usage?.OutputTokenCount ?? 0;
        }
    }

    public void RecordLlmError()
    {
        lock (_gate)
        {
            _llmErrors++;
        }
    }

    public void RecordEmbeddingCall()
    {
        lock (_gate)
        {
            _embeddingCalls++;
        }
    }

    public MetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new MetricsSnapshot
            {
                Queries = _queries,
                Answered = _answered,
                Blocked = _blocked,
                Refused = _refused,
                NoMatches = _noMatches,
                Routes = new Dictionary<string, int>(_routes, StringComparer.OrdinalIgnoreCase),
                ExactCacheHits = _exactCacheHits,
                SemanticCacheHits = _semanticCacheHits,
                CacheMisses = _cacheMisses,
                GuardrailBlocks = new Dictionary<string, int>(_guardrailBlocks, StringComparer.Ordinal),
                LlmCalls = _llmCalls,
                LlmErrors = _llmErrors,
                EmbeddingCalls = _embeddingCalls,
                InputTokens = _inputTokens,
                OutputTokens = _outputTokens,
                AverageQueryLatencyMs = _queries == 0 ? 0 : _queryLatencyMsTotal / _queries,
            };
        }
    }

    /// <summary>
    /// Human-readable report for the interactive "/stats" command.
    /// </summary>
    public string RenderReport()
    {
        var s = Snapshot();
        var sb = new StringBuilder();

        sb.AppendLine("Pipeline metrics (this session)");
        sb.AppendLine("-------------------------------");
        sb.AppendLine($"Queries:            {s.Queries}  (answered {s.Answered}, blocked {s.Blocked}, refused {s.Refused}, no matches {s.NoMatches})");
        sb.AppendLine($"Avg query latency:  {s.AverageQueryLatencyMs:F0} ms");
        sb.AppendLine($"Cache:              {s.ExactCacheHits} exact hits, {s.SemanticCacheHits} semantic hits, {s.CacheMisses} misses");

        sb.Append("Routes:             ");
        sb.AppendLine(s.Routes.Count == 0
            ? "none yet"
            : string.Join(", ", s.Routes.Select(kv => $"{kv.Key} x{kv.Value}")));

        sb.Append("Guardrail blocks:   ");
        sb.AppendLine(s.GuardrailBlocks.Count == 0
            ? "none"
            : string.Join(", ", s.GuardrailBlocks.Select(kv => $"{kv.Key} x{kv.Value}")));

        sb.AppendLine($"LLM calls:          {s.LlmCalls}  (errors {s.LlmErrors})");
        sb.AppendLine($"Embedding calls:    {s.EmbeddingCalls}");
        sb.AppendLine($"Tokens (in/out):    {s.InputTokens} / {s.OutputTokens}");

        return sb.ToString();
    }
}
