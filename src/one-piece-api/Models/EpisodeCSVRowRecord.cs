using CsvHelper.Configuration.Attributes;

namespace OnePieceApi.Models;

/// <summary>
/// Represents a row in the CSV file containing episode data, including title, overview, and arc information.
/// </summary>
public sealed record EpisodeCsvRowRecord
{
    [Name("name")]
    public string Name { get; set; } = string.Empty;

    [Name("season")]
    public int Season { get; set; }

    [Name("episode")]
    public int Episode { get; set; }

    [Name("start")]
    public int StartYear { get; set; }

    [Name("average_rating")]
    public float AverageRating { get; set; }
}