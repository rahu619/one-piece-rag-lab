using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace one_piece_api.Tests.Mocks;

/// <summary>
/// A mock chat client that simulates LLM intent classification responses for unit testing.
/// </summary>
public class MockChatClient : IChatClient
{
    public void Dispose()
    {
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lastMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var replyText = "GENERAL";

        // Parse the user query from the routing classification prompt
        var queryIndex = lastMessage.IndexOf("Query: ");
        var query = lastMessage;
        if (queryIndex != -1)
        {
            var substring = lastMessage.Substring(queryIndex + 7);
            var categoryIndex = substring.IndexOf("\nCategory:");
            if (categoryIndex != -1)
            {
                query = substring.Substring(0, categoryIndex).Trim();
            }
            else
            {
                query = substring.Trim();
            }
        }

        if (IsOnePiece(query))
        {
            replyText = "ONE_PIECE";
        }

        var chatMessage = new ChatMessage(ChatRole.Assistant, replyText);
        var chatResponse = new ChatResponse(chatMessage);
        return Task.FromResult(chatResponse);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    private static bool IsOnePiece(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        string[] keywords = { "zoro", "luffy", "episode", "season", "rating", "shanks", "straw hat", "romance dawn" };
        return keywords.Any(k => normalized.Contains(k));
    }
}
