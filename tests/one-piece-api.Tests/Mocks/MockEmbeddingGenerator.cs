using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace one_piece_api.Tests.Mocks;

/// <summary>
/// A mock embedding generator that yields deterministic vectors based on input categories for unit testing.
/// </summary>
public class MockEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public void Dispose()
    {
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var val in values)
        {
            var vector = GetDeterministicMockVector(val);
            list.Add(new Embedding<float>(vector));
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(list);
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    private static float[] GetDeterministicMockVector(string text)
    {
        var vec = new float[1536];
        var normalized = text.Trim().ToLowerInvariant();
        normalized = new string(normalized.Where(c => !char.IsPunctuation(c)).ToArray()).Trim();

        if (IsGreeting(normalized))
        {
            vec[0] = 1.0f; // greetings point along dimension 0
        }
        else if (IsGratitude(normalized))
        {
            vec[1] = 1.0f; // gratitude points along dimension 1
        }
        else if (IsBotInfo(normalized))
        {
            vec[2] = 1.0f; // bot info points along dimension 2
        }
        else if (IsStatus(normalized))
        {
            vec[3] = 1.0f; // status points along dimension 3
        }
        else
        {
            vec[4] = 1.0f; // general search queries point along dimension 4
        }

        return vec;
    }

    private static bool IsGreeting(string text)
    {
        string[] greetings = { "hello", "hi", "hey", "yo", "greetings", "good morning", "good afternoon", "good evening", "howdy", "hola", "sup" };
        return greetings.Contains(text);
    }

    private static bool IsGratitude(string text)
    {
        string[] gratitude = { "thanks", "thank you", "cheers", "awesome", "perfect", "ok", "okay", "thank you very much" };
        return gratitude.Contains(text);
    }

    private static bool IsBotInfo(string text)
    {
        string[] botInfo = { "who are you", "what are you", "what is your name", "what can you do", "what do you do", "help", "info", "tell me about yourself" };
        return botInfo.Contains(text) || text.Contains("who are you") || text.Contains("what is your name") || text.Contains("what can you do") || text.Contains("what do you do");
    }

    private static bool IsStatus(string text)
    {
        string[] status = { "how are you", "how are you doing", "how's it going", "hows it going" };
        return status.Contains(text) || text.Contains("how are you") || text.Contains("hows it going") || text.Contains("how's it going");
    }
}
