using System;
using System.Collections.Generic;
using System.Linq;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Service for filtering trivial user inputs (greetings, gratitude, etc.) before running RAG database/LLM calls.
/// </summary>
public class InputFilterService
{
    private static readonly HashSet<string> Greetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "yo", "greetings", "good morning", "good afternoon", "good evening", "howdy", "hola", "sup"
    };

    private static readonly HashSet<string> SimpleConversations = new(StringComparer.OrdinalIgnoreCase)
    {
        "who are you", "what are you", "what is your name", "what can you do", "what do you do",
        "how are you", "how are you doing", "how's it going", "hows it going",
        "thanks", "thank you", "cheers", "awesome", "perfect", "ok", "okay",
        "help", "info"
    };

    /// <summary>
    /// Checks if the query is a simple greeting or conversation, and returns a pre-defined response if so.
    /// </summary>
    public (bool IsFiltered, string Response) FilterInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (true, "Please ask a question about One Piece!");
        }

        // Normalize input: trim, lowercase, remove punctuation
        var normalized = input.Trim().ToLowerInvariant();
        normalized = RemovePunctuation(normalized);

        if (Greetings.Contains(normalized))
        {
            return (true, "Hello! I am your One Piece assistant. How can I help you today?");
        }

        if (SimpleConversations.Contains(normalized))
        {
            if (normalized.Contains("thank") || normalized == "thanks" || normalized == "cheers" || normalized == "perfect" || normalized == "awesome")
            {
                return (true, "You're welcome! Let me know if you have any other questions about One Piece.");
            }
            if (normalized.Contains("who are you") || normalized.Contains("what is your name") || normalized.Contains("what can you do") || normalized.Contains("what do you do") || normalized == "help" || normalized == "info")
            {
                return (true, "I am a One Piece retrieval assistant. You can ask me questions about One Piece episodes, ratings, release years, or overviews, and I will find the relevant information for you!");
            }
            if (normalized.Contains("how are you") || normalized.Contains("hows it going") || normalized.Contains("how's it going"))
            {
                return (true, "I'm doing great, thank you! Ready to search for some One Piece episodes. What would you like to know?");
            }

            return (true, "I'm here to help you search for One Piece episodes and ratings. Ask me a question!");
        }

        return (false, string.Empty);
    }

    private static string RemovePunctuation(string text)
    {
        return new string(text.Where(c => !char.IsPunctuation(c)).ToArray()).Trim();
    }
}
