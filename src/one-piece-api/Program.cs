using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OllamaSharp;
using OnePieceApi.Config;
using OnePieceApi.Ingestion;
using OnePieceApi.Models;
using OnePieceApi.Retrieval;
using Qdrant.Client;

using static System.Console;


var builder = Host.CreateApplicationBuilder(args);

var ollamaOptions = builder.Configuration.GetRequiredSection("Ollama").Get<OllamaOptions>();
var ingestionOptions = builder.Configuration.GetRequiredSection("Ingestion").Get<IngestionOptions>();

// Setup Ollama endpoint configuration
var ollamaUri = new Uri(ollamaOptions!.BaseUrl);
var llmModelId = ollamaOptions.ModelId; // general LLM like llama3.2 / mistral for generation

// Register the Ollama-based embedding generator
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OllamaApiClient(ollamaUri, llmModelId));

// Register the IChatClient for answering queries
builder.Services.AddSingleton<IChatClient>(sp => new OllamaApiClient(ollamaUri, llmModelId));

// Get the host from environment variables (will be "qdrant" in Docker) or default to "localhost" if running locally.
var qdrantHost = Environment.GetEnvironmentVariable("QDRANT_HOST") ?? "localhost";

builder.Services.AddSingleton(new QdrantClient(qdrantHost, 6334));

builder.Services.AddSingleton<VectorStore>(sp =>
{
    var client = sp.GetRequiredService<QdrantClient>();
    return new QdrantVectorStore(client, true);
});

// Bind and register SemanticCacheOptions
var cacheOptions = builder.Configuration.GetSection("SemanticCache").Get<SemanticCacheOptions>() ?? new SemanticCacheOptions();
builder.Services.AddSingleton(cacheOptions);



builder.Services.AddSingleton<VectorStoreCollection<ulong, EpisodeRecord>>(sp =>
{
    var store = sp.GetRequiredService<VectorStore>();
    return store.GetCollection<ulong, EpisodeRecord>("one_piece_episodes");
});

builder.Services.AddSingleton<VectorStoreCollection<ulong, CacheRecord>>(sp =>
{
    var store = sp.GetRequiredService<VectorStore>();
    return store.GetCollection<ulong, CacheRecord>(cacheOptions.CollectionName);
});

builder.Services.AddTransient<DatasetIngestor>();
builder.Services.AddTransient<SearchService>();
builder.Services.AddSingleton<SemanticCacheService>();
builder.Services.AddSingleton<SqliteDatabaseService>();

var host = builder.Build();

var ingestor = host.Services.GetRequiredService<DatasetIngestor>();
var searchService = host.Services.GetRequiredService<SearchService>();
var chatClient = host.Services.GetRequiredService<IChatClient>(); // Get the LLM client
var embeddingGenerator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var semanticCache = host.Services.GetRequiredService<SemanticCacheService>();

// Ensure the cache collection exists
if (cacheOptions.Enabled)
{
    var cacheCollection = host.Services.GetRequiredService<VectorStoreCollection<ulong, CacheRecord>>();
    await cacheCollection.EnsureCollectionExistsAsync();
}



WriteLine("One-piece-api Node Initialized.");

if (ingestionOptions!.RunOnStart && !string.IsNullOrEmpty(ingestionOptions.CsvFilePath))
{
    WriteLine($"Ingestion triggered on start via configuration. Target: {ingestionOptions.CsvFilePath}");
    try
    {
        await ingestor.IngestCsvAsync(ingestionOptions.CsvFilePath);
    }
    catch (VectorStoreException ex)
    {
        WriteLine($"Vector Store Error: {ex.Message}");
        if (ex.InnerException != null)
        {
            WriteLine($"Inner Exception: {ex.InnerException.Message}");
        }
    }
}
else
{
    WriteLine("Data ingestion skipped. Using current vector data inside database index.");
}

    while (true)
    {
        ForegroundColor = ConsoleColor.Cyan;
        Write("\nEnter search query: ");
        ResetColor();

        string? query = ReadLine();
        if (string.IsNullOrWhiteSpace(query)) break;

        // 1) Precompute query embedding for the semantic cache lookup
        var queryEmbeddingResult = await embeddingGenerator.GenerateAsync(query);
        var queryVector = queryEmbeddingResult.Vector;

        bool isHit = false;
        string? cachedAnswer = null;
        List<EpisodeRecord>? cachedSources = null;
        double? similarityScore = null;

        if (cacheOptions.Enabled)
        {
            (isHit, cachedAnswer, cachedSources, similarityScore) = await semanticCache.GetCachedResponseAsync(
                query,
                queryVector,
                cacheOptions.SimilarityThreshold);
        }

        if (isHit && cachedAnswer != null && cachedSources != null)
        {
            ForegroundColor = ConsoleColor.Magenta;
            WriteLine($"\n[Semantic Cache Hit] (Similarity: {similarityScore:F4})");
            ResetColor();

            ForegroundColor = ConsoleColor.Green;
            Write("\n[Answer]: ");
            Write(cachedAnswer);
            WriteLine();
            ResetColor();

            if (cachedSources.Any())
            {
                WriteLine("\n--- Sources Used ---");
                foreach (var episode in cachedSources)
                {
                    WriteLine($"* {episode.Title} (Rating: {episode.Rating})");
                }
            }
            continue;
        }

        // Cache Miss -> Process via Intent Routing
        if (cacheOptions.Enabled)
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine("\n[Semantic Cache Miss] Processing query...");
            ResetColor();
        }

        // 2) Classify the query intent using the LLM router
        var classificationPrompt = $"""
            You are a query router. Classify the user query into exactly one of three categories:
            - "SQL" (if the query requires analytical or quantitative calculations, counts, averages, sorting, or groupings about ratings, episodes, seasons, or release years)
            - "VECTOR" (if the query is asking about character descriptions, storylines, plot details, relationships, or what happens in the episodes)
            - "GENERAL" (if the query is a greeting, general chitchat, help request, or a general knowledge/coding question not about One Piece)

            Respond with exactly one word: either "SQL", "VECTOR", or "GENERAL". Do not write anything else.

            Query: {query}
            Category:
            """;

        ForegroundColor = ConsoleColor.Yellow;
        WriteLine("Routing query...");
        ResetColor();

        var routingResponse = await chatClient.GetResponseAsync(classificationPrompt);
        var intent = routingResponse.Text.Trim().ToUpperInvariant();

        if (intent.Contains("SQL"))
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine("\n[Route: SQL Database Query]");
            ResetColor();

            var sqlPrompt = $"""
                You are a SQLite query generator. Write a single SQLite SELECT statement to answer the user query.
                Database schema:
                Table: Episodes (
                    Id INT,
                    Title TEXT,
                    Overview TEXT,
                    Season INT,
                    EpisodeNumber INT,
                    ReleaseYear INT,
                    Rating REAL
                )

                Respond with ONLY the raw SQL query. Do not write markdown, explanations, or any other text.

                Query: {query}
                SQL:
                """;

            var sqlGenResponse = await chatClient.GetResponseAsync(sqlPrompt);
            var sqlQuery = sqlGenResponse.Text.Trim();
            
            // Clean up code block formats
            if (sqlQuery.StartsWith("```sql")) sqlQuery = sqlQuery.Substring(6);
            if (sqlQuery.StartsWith("```")) sqlQuery = sqlQuery.Substring(3);
            if (sqlQuery.EndsWith("```")) sqlQuery = sqlQuery.Substring(0, sqlQuery.Length - 3);
            sqlQuery = sqlQuery.Trim();

            ForegroundColor = ConsoleColor.Blue;
            WriteLine($"Executing SQL: {sqlQuery}");
            ResetColor();

            var dbService = host.Services.GetRequiredService<SqliteDatabaseService>();
            var sqlResult = dbService.ExecuteSqlQuery(sqlQuery);

            var answerPrompt = $"""
                You are an expert One Piece assistant. Answer the user's question accurately using the structured SQLite database results provided below.

                Database Results:
                {sqlResult}

                User Question: {query}
                Answer:
                """;

            ForegroundColor = ConsoleColor.Green;
            Write("\n[Answer]: ");
            var responseStream = chatClient.GetStreamingResponseAsync(answerPrompt);
            var sb = new System.Text.StringBuilder();
            await foreach (var update in responseStream)
            {
                Write(update.Text);
                sb.Append(update.Text);
            }
            WriteLine();
            ResetColor();

            if (cacheOptions.Enabled)
            {
                await semanticCache.SaveToCacheAsync(query, queryVector, sb.ToString(), new List<EpisodeRecord>());
            }
        }
        else if (intent.Contains("VECTOR"))
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine("\n[Route: Semantic Vector Search]");
            ResetColor();

            var matches = await searchService.SearchAsync(queryVector, limit: 5);
            if (!matches.Any())
            {
                WriteLine("No matching episodes found.");
                continue;
            }

            var sortedMatches = matches.OrderByDescending(e => e.Rating).ToList();
            var contextData = string.Join("\n", sortedMatches.Select(e =>
                $"- Title: {e.Title}, Season: {e.Season}, Episode: {e.EpisodeNumber}, Year: {e.ReleaseYear}, Rating: {e.Rating}\n  Overview: {e.Overview}"));

            var answerPrompt = $"""
                You are an expert One Piece assistant. Answer the user's question accurately using ONLY the provided context dataset below.

                Context Dataset (ordered from highest rating to lowest rating):
                {contextData}

                User Question: {query}
                Answer:
                """;

            ForegroundColor = ConsoleColor.Green;
            Write("\n[Answer]: ");
            var responseStream = chatClient.GetStreamingResponseAsync(answerPrompt);
            var sb = new System.Text.StringBuilder();
            await foreach (var update in responseStream)
            {
                Write(update.Text);
                sb.Append(update.Text);
            }
            WriteLine();
            ResetColor();

            if (cacheOptions.Enabled)
            {
                await semanticCache.SaveToCacheAsync(query, queryVector, sb.ToString(), sortedMatches);
            }

            WriteLine("\n--- Sources Used ---");
            foreach (var episode in sortedMatches)
            {
                WriteLine($"* {episode.Title} (Rating: {episode.Rating})");
            }
        }
        else
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine("\n[Route: General Conversation]");
            ResetColor();

            ForegroundColor = ConsoleColor.Green;
            Write("\n[Answer]: ");
            var responseStream = chatClient.GetStreamingResponseAsync(query);
            var sb = new System.Text.StringBuilder();
            await foreach (var update in responseStream)
            {
                Write(update.Text);
                sb.Append(update.Text);
            }
            WriteLine();
            ResetColor();

            if (cacheOptions.Enabled)
            {
                await semanticCache.SaveToCacheAsync(query, queryVector, sb.ToString(), new List<EpisodeRecord>());
            }
        }
    }