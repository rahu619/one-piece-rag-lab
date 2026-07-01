using System.Globalization;
using CsvHelper;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;
using static System.Console;

namespace OnePieceApi.Ingestion;

/// <summary>
/// Ingests episode data from a CSV file into a vector store, generating embeddings for the episode overviews for semantic search.
/// </summary>
/// <param name="embeddingGenerator"></param>
/// <param name="collection"></param>
public class DatasetIngestor(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    VectorStoreCollection<ulong, EpisodeRecord> collection,
    OnePieceApi.Retrieval.SqliteDatabaseService sqliteDatabaseService)
{

    private const int BatchSize = 50;

    /// <summary>
    /// Ingests episode data from a CSV file, generating embeddings for the overviews and storing them in the vector store collection and SQLite.
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns></returns>
    public async Task IngestCsvAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Comment out the following lines later to preserve existing data in the collection. For now, we want to start fresh for testing purposes.
        //Delete the entire collection along with all its vectors and indexes
        await collection.EnsureCollectionDeletedAsync(cancellationToken);
        WriteLine("Collection deleted successfully.");

        // Re-create it fresh using your EpisodeRecord schema attributes
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        WriteLine("Fresh collection recreated and ready for indexing!");

        // Initialize SQL database fresh
        sqliteDatabaseService.InitializeDatabase();
        WriteLine("SQLite database initialized successfully.");

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        var records = csv.GetRecordsAsync<EpisodeCsvRowRecord>(cancellationToken).Take(40);
        var batch = new List<EpisodeCsvRowRecord>();
        ulong idCounter = 1;

        await foreach (var row in records.WithCancellation(cancellationToken))
        {
            batch.Add(row);

            if (batch.Count >= BatchSize)
            {
                await ProcessAndUpsertBatchAsync(batch, idCounter, cancellationToken);
                idCounter += (ulong)batch.Count;
                batch.Clear();
            }
        }

        // Process any remaining records
        if (batch.Count > 0)
        {
            await ProcessAndUpsertBatchAsync(batch, idCounter, cancellationToken);
        }
    }

    private async Task ProcessAndUpsertBatchAsync(List<EpisodeCsvRowRecord> batch, ulong startingId, CancellationToken cancellationToken)
    {
        // 1) Prepare texts for parallel embedding generation
        var embeddingTasks = batch.Select(async (row, index) =>
        {
            // Clean semantic text focus solely on textual relevance
            string textToEmbed = $"Episode Title: {row.Name}. This episode belongs to Season {row.Season}.";

            var embeddingResult = await embeddingGenerator.GenerateAsync(textToEmbed, null, cancellationToken);

            return new EpisodeRecord
            {
                Id = startingId + (ulong)index,
                Title = row.Name,
                Overview = textToEmbed,
                Season = row.Season,          
                EpisodeNumber = row.Episode,   
                ReleaseYear = row.StartYear,  
                Rating = row.AverageRating,    
                OverviewEmbedding = embeddingResult.Vector
            };
        });

        // 2) Execute all embedding generation HTTP calls concurrently
        EpisodeRecord[] recordsToUpsert = await Task.WhenAll(embeddingTasks);

        // 3) Batch upsert into Qdrant in a single database network call
        // Depending on your SDK version, you can loop or use a native batch API if exposed:
        foreach (var record in recordsToUpsert)
        {
            await collection.UpsertAsync(record, cancellationToken);
            sqliteDatabaseService.InsertEpisode(record);
        }

        WriteLine($"[Ingestor] Successfully processed and batched index slice: {batch.Count} elements.");
    }
}