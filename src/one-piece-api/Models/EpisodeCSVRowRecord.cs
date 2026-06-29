namespace OnePieceApi.Models;

/// <summary>
/// Represents a row in the CSV file containing episode data, including title, overview, and arc information.
/// </summary>
public sealed record EpisodeCsvRowRecord
{
    /// <summary>
    /// The title of the episode.
    /// </summary>
    public string Title { get; set; } = default!;
    
    /// <summary>
    /// The overview or summary of the episode.
    /// </summary>
    public string Overview { get; set; } = default!;
    
    /// <summary>
    /// The arc to which the episode belongs. This field is optional and may be null.
    /// </summary>
    public string? Arc { get; set; }
}