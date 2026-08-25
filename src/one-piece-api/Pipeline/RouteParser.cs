namespace OnePieceApi.Pipeline;

/// <summary>
/// Deterministic parsing helpers for LLM route and SQL outputs. Kept separate from the
/// engine so they stay unit-testable.
/// </summary>
public static class RouteParser
{
    /// <summary>
    /// Maps the router model's raw text to a route name. Small models echo their prompt, and
    /// the router prompt names every category, so searching the whole response with Contains
    /// would always match the first-listed category — match the leading token instead.
    /// </summary>
    public static string ParseIntent(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return "GENERAL";
        }

        var firstToken = responseText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim('"', '\'', '.', ':', '*', '`')
            .ToUpperInvariant();

        return firstToken is "SQL" or "VECTOR" ? firstToken : "GENERAL";
    }

    /// <summary>
    /// Removes markdown code fences the SQL model sometimes wraps its query in.
    /// </summary>
    public static string StripCodeFence(string text)
    {
        var sql = text.Trim();

        if (sql.StartsWith("```sql", StringComparison.OrdinalIgnoreCase)) sql = sql[6..];
        else if (sql.StartsWith("```")) sql = sql[3..];
        if (sql.EndsWith("```")) sql = sql[..^3];

        return sql.Trim();
    }
}
