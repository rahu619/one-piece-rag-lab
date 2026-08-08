using System.Globalization;
using CsvHelper;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using OnePieceApi.Models;
using OnePieceApi.Retrieval;
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
    SqliteDatabaseService sqliteDatabaseService)
{

    private const int BatchSize = 50;

    /// <summary>
    /// Ingests episode data from a CSV file, generating embeddings for the overviews and storing them in the vector store collection and SQLite.
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns></returns>
    public async Task IngestCsvAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // Delete the entire collection along with all its vectors and indexes so the
        // ingestion lifecycle matches the relational side, which is also rebuilt below.
        await collection.EnsureCollectionDeletedAsync(cancellationToken);
        WriteLine("Collection deleted successfully.");

        // Re-create it fresh using your EpisodeRecord schema attributes
        await collection.EnsureCollectionExistsAsync(cancellationToken);
        WriteLine("Fresh collection recreated and ready for indexing!");

        // Initialize SQL database fresh
        await sqliteDatabaseService.InitializeDatabaseAsync(cancellationToken);
        WriteLine("SQLite database initialized successfully.");

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        var records = csv.GetRecordsAsync<EpisodeCsvRowRecord>(cancellationToken);
        var batch = new List<EpisodeCsvRowRecord>(BatchSize);
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
        // 1) Clean semantic text focused solely on textual relevance
        var textsToEmbed = batch
            .Select(row => $"Episode Title: {row.Name}. This episode belongs to Season {row.Season}.")
            .ToList();

        // 2) One batched embedding request per slice. Firing a request per row instead
        // just queues behind Ollama's own parallelism limit while holding open sockets.
        var embeddings = await embeddingGenerator.GenerateAsync(textsToEmbed, null, cancellationToken);

        var recordsToUpsert = new List<EpisodeRecord>(batch.Count);
        for (var index = 0; index < batch.Count; index++)
        {
            var row = batch[index];
            recordsToUpsert.Add(new EpisodeRecord
            {
                Id = startingId + (ulong)index,
                Title = row.Name,
                Overview = textsToEmbed[index],
                Season = row.Season,
                EpisodeNumber = row.Episode,
                ReleaseYear = row.StartYear,
                Rating = row.AverageRating,
                OverviewEmbedding = embeddings[index].Vector
            });
        }

        // 3) Batch upsert into Qdrant in a single network call, then insert the relational
        // copy in a single transaction.
        await collection.UpsertAsync(recordsToUpsert, cancellationToken);
        await sqliteDatabaseService.InsertEpisodesAsync(recordsToUpsert, cancellationToken);

        WriteLine($"[Ingestor] Successfully processed and batched index slice: {batch.Count} elements.");
    }
}
