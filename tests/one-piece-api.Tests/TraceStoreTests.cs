using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OnePieceApi.Config;
using OnePieceApi.Observability;
using Xunit;

namespace one_piece_api.Tests;

/// <summary>
/// The trace store is the observability audit trail; these tests pin its on-disk contract.
/// </summary>
public class TraceStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"one_piece_traces_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static TraceEvent SampleEvent(string traceId) => new()
    {
        TraceId = traceId,
        Timestamp = DateTimeOffset.UtcNow,
        Kind = "llm",
        Name = "routing",
        Status = "ok",
        LatencyMs = 123.4,
        Model = "qwen2.5-coder:1.5b",
        Input = "What is the best episode?",
        Output = "SQL",
    };

    [Fact]
    public void Write_AppendsOneJsonLinePerEvent()
    {
        var store = new TraceStore(new ObservabilityOptions { TraceDirectory = _tempDir }, new SafetyOptions());

        store.Write(SampleEvent("trace-1"));
        store.Write(SampleEvent("trace-2"));

        var file = Directory.GetFiles(store.TracesDirectory).Single();
        var lines = File.ReadAllLines(file);

        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("trace-1", doc.RootElement.GetProperty("traceId").GetString());
        Assert.Equal("llm", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("SQL", doc.RootElement.GetProperty("output").GetString());
    }

    [Fact]
    public void Write_IsANoOpWhenDisabled()
    {
        var store = new TraceStore(new ObservabilityOptions { Enabled = false, TraceDirectory = _tempDir }, new SafetyOptions());

        store.Write(SampleEvent("trace-1"));

        Assert.False(Directory.Exists(store.TracesDirectory));
    }

    [Fact]
    public void PrepareTextForLog_DropsTextWhenPromptLoggingIsOff()
    {
        var store = new TraceStore(new ObservabilityOptions { LogPrompts = false }, new SafetyOptions());

        Assert.Null(store.PrepareTextForLog("anything"));
    }

    [Fact]
    public void PrepareTextForLog_RedactsPiiWhenConfigured()
    {
        var store = new TraceStore(new ObservabilityOptions(), new SafetyOptions { RedactPiiInTraces = true });

        var prepared = store.PrepareTextForLog("email me at jane.doe@example.com");

        Assert.DoesNotContain("jane.doe@example.com", prepared);
        Assert.Contains("[REDACTED_EMAIL]", prepared);
    }
}
