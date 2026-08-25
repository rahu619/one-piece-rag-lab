using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using OnePieceApi.Config;

namespace OnePieceApi.Observability;

/// <summary>
/// Wraps every chat and embedding call with tracing and metrics: latency, token usage,
/// model id, and (configurably) PII-redacted prompt/completion text. The query engine calls
/// everything through this class, so no LLM interaction can bypass observability.
/// </summary>
public class LlmInstrumentation(
    TraceStore traces,
    PipelineMetrics metrics,
    OllamaOptions ollamaOptions)
{
    public async Task<ChatResponse> ChatAsync(
        IChatClient client,
        string traceId,
        string operation,
        string prompt,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await client.GetResponseAsync(prompt, chatOptions, cancellationToken);
            stopwatch.Stop();

            var usage = response.Usage;
            metrics.RecordLlmCall(stopwatch.Elapsed, usage);

            traces.Write(new TraceEvent
            {
                TraceId = traceId,
                Timestamp = DateTimeOffset.UtcNow,
                Kind = "llm",
                Name = operation,
                Status = "ok",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Model = ollamaOptions.ModelId,
                InputTokens = usage?.InputTokenCount,
                OutputTokens = usage?.OutputTokenCount,
                Input = traces.PrepareTextForLog(prompt),
                Output = traces.PrepareTextForLog(response.Text ?? string.Empty),
            });

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            metrics.RecordLlmError();

            traces.Write(new TraceEvent
            {
                TraceId = traceId,
                Timestamp = DateTimeOffset.UtcNow,
                Kind = "llm",
                Name = operation,
                Status = "error",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Model = ollamaOptions.ModelId,
                Input = traces.PrepareTextForLog(prompt),
                Error = ex.Message,
            });

            throw;
        }
    }

    /// <summary>
    /// Streams a completion token-by-token through <paramref name="onToken"/> while still
    /// recording a full trace span. Streaming updates carry no usage details in M.E.AI, so
    /// Usage is always null on this path; token counts are only available for the
    /// non-streaming routing and SQL-generation calls.
    /// </summary>
    public async Task<(string Text, UsageDetails? Usage)> StreamChatAsync(
        IChatClient client,
        string traceId,
        string operation,
        string prompt,
        IProgress<string>? onToken = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        UsageDetails? usage = null;

        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
            {
                text.Append(update.Text);
                onToken?.Report(update.Text);
            }

            stopwatch.Stop();
            metrics.RecordLlmCall(stopwatch.Elapsed, usage);

            traces.Write(new TraceEvent
            {
                TraceId = traceId,
                Timestamp = DateTimeOffset.UtcNow,
                Kind = "llm",
                Name = operation,
                Status = "ok",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Model = ollamaOptions.ModelId,
                InputTokens = usage?.InputTokenCount,
                OutputTokens = usage?.OutputTokenCount,
                Input = traces.PrepareTextForLog(prompt),
                Output = traces.PrepareTextForLog(text.ToString()),
            });

            return (text.ToString(), usage);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            metrics.RecordLlmError();

            traces.Write(new TraceEvent
            {
                TraceId = traceId,
                Timestamp = DateTimeOffset.UtcNow,
                Kind = "llm",
                Name = operation,
                Status = "error",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Model = ollamaOptions.ModelId,
                Input = traces.PrepareTextForLog(prompt),
                Error = ex.Message,
            });

            throw;
        }
    }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string traceId,
        string operation,
        string text,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var embedding = await generator.GenerateAsync(text, cancellationToken: cancellationToken);
            stopwatch.Stop();
            metrics.RecordEmbeddingCall();

            traces.Write(new TraceEvent
            {
                TraceId = traceId,
                Timestamp = DateTimeOffset.UtcNow,
                Kind = "embedding",
                Name = operation,
                Status = "ok",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Model = ollamaOptions.EmbeddingModelId,
                Input = traces.PrepareTextForLog(text),
            });

            return embedding.Vector;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            traces.Write(new TraceEvent
            {
                TraceId = traceId,
                Timestamp = DateTimeOffset.UtcNow,
                Kind = "embedding",
                Name = operation,
                Status = "error",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Model = ollamaOptions.EmbeddingModelId,
                Input = traces.PrepareTextForLog(text),
                Error = ex.Message,
            });

            throw;
        }
    }
}
