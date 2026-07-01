using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace OnePieceApi.Retrieval;

public enum QueryIntent
{
    OnePiece,
    General
}

/// <summary>
/// Service for routing queries using LLM-based intent classification.
/// </summary>
public class InputFilterService(IChatClient chatClient)
{
    /// <summary>
    /// Classifies the user query using the LLM to decide if it is a One Piece RAG query or a General query.
    /// </summary>
    public async Task<QueryIntent> ClassifyQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return QueryIntent.General;
        }

        var classificationPrompt = $"""
            You are a query router. Classify the user query into exactly one of two categories:
            - "ONE_PIECE" (if the query is asking about the anime/manga One Piece, episodes, characters, plot, ratings, etc.)
            - "GENERAL" (if the query is a greeting, general chitchat, help request, or a general knowledge/coding/math/physics/geography question not about One Piece)

            Respond with exactly one word: either "ONE_PIECE" or "GENERAL". Do not write anything else.

            Query: {query}
            Category:
            """;

        try
        {
            var response = await chatClient.GetResponseAsync(classificationPrompt, null, cancellationToken);
            var result = response.Text.Trim();

            if (result.Contains("ONE_PIECE", StringComparison.OrdinalIgnoreCase))
            {
                return QueryIntent.OnePiece;
            }
        }
        catch
        {
            // Fallback to OnePiece (trigger RAG) in case of errors
            return QueryIntent.OnePiece;
        }

        return QueryIntent.General;
    }
}
