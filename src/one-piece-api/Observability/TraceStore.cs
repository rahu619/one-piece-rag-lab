using System.Text.Json;
using System.Text.Json.Serialization;
using OnePieceApi.Config;
using OnePieceApi.Safety;

namespace OnePieceApi.Observability;

/// <summary>
/// One span of pipeline activity, serialized as a single JSONL line.
/// </summary>
public sealed record TraceEvent
{
    public required string TraceId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>"query", "llm", or "embedding".</summary>
    public required string Kind { get; init; }

    /// <summary>Operation name, e.g. "routing", "sql_generation", "answer", "query_embedding".</summary>
    public required string Name { get; init; }

    /// <summary>"ok", "error", or "blocked".</summary>
    public required string Status { get; init; }

    public double? LatencyMs { get; init; }

    public string? Model { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    /// <summary>Prompt or query text, present only when prompt logging is enabled.</summary>
    public string? Input { get; init; }

    /// <summary>Completion text, present only when prompt logging is enabled.</summary>
    public string? Output { get; init; }

    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Attributes { get; init; }
}

/// <summary>
/// Appends trace events as JSONL, one file per UTC day under the configured directory.
/// JSONL is append-only and greppable, and it can be shipped to any log pipeline later
/// (OpenTelemetry, Langfuse, a data warehouse) without schema changes.
/// </summary>
public class TraceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ObservabilityOptions _options;
    private readonly SafetyOptions _safety;
    private readonly object _gate = new();

    public TraceStore(ObservabilityOptions options, SafetyOptions safety)
    {
        _options = options;
        _safety = safety;
    }

    public bool Enabled => _options.Enabled;

    public string TracesDirectory =>
        Path.IsPathRooted(_options.TraceDirectory)
            ? Path.Combine(_options.TraceDirectory, "traces")
            : Path.Combine(Directory.GetCurrentDirectory(), _options.TraceDirectory, "traces");

    public void Write(TraceEvent evt)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var line = JsonSerializer.Serialize(evt, SerializerOptions);

        lock (_gate)
        {
            Directory.CreateDirectory(TracesDirectory);
            var file = Path.Combine(TracesDirectory, $"trace-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    /// <summary>
    /// Prepares prompt or completion text for a trace event: dropped entirely when prompt
    /// logging is off, PII-redacted when that safety setting is on.
    /// </summary>
    public string? PrepareTextForLog(string text)
    {
        if (!_options.LogPrompts)
        {
            return null;
        }

        return _safety.RedactPiiInTraces ? PiiRedactor.Redact(text) : text;
    }
}
