using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

using OnePieceApi.Config;

namespace OnePieceApi.Retrieval;

public enum QueryIntent
{
    OnePiece,
    General
}

/// <summary>
/// Service for routing queries using LLM-based intent classification.
/// </summary>
public class InputFilterService(IChatClient chatClient, InputFilterOptions options)
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

        var promptTemplate = string.IsNullOrWhiteSpace(options.ClassificationPrompt)
            ? "You are a query router. Classify: {query}. Respond ONE_PIECE or GENERAL."
            : options.ClassificationPrompt;

        var classificationPrompt = promptTemplate.Replace("{query}", query);

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
