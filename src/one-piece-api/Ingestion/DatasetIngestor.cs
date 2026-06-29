using System.Globalization;
using CsvHelper;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;

namespace OnePieceApi.Ingestion;

/// <summary>
/// Ingests episode data from a CSV file into a vector store, generating embeddings for the episode overviews for semantic search.
/// </summary>
/// <param name="embeddingGenerator"></param>
/// <param name="collection"></param>
public class DatasetIngestor(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStoreCollection<ulong, EpisodeRecord> collection)
{
    /// <summary>
    /// Ingests episode data from a CSV file, generating embeddings for the overviews and storing them in the vector store collection.
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns></returns>
    public async Task IngestCsvAsync(string filePath)
    {
        await collection.EnsureCollectionExistsAsync();

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        var records = csv.GetRecordsAsync<EpisodeCsvRowRecord>();
        ulong idCounter = 1;

        await foreach (var row in records)
        {
            string overview = row.Overview;
            string title = row.Title;

            var embeddingResult = await embeddingGenerator.GenerateAsync(overview);

            var episode = new EpisodeRecord
            {
                Id = idCounter++,
                Title = title,
                Overview = overview,
                Arc = row.Arc ?? "Unknown",
                OverviewEmbedding = embeddingResult.Vector
            };

            await collection.UpsertAsync(episode);
            Console.WriteLine($"Successfully Indexed: {title}");
        }
    }
}