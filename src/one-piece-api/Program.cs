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

// Bind and register InputFilterOptions
var filterOptions = builder.Configuration.GetSection("InputFilter").Get<InputFilterOptions>() ?? new InputFilterOptions();
builder.Services.AddSingleton(filterOptions);

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
builder.Services.AddSingleton<InputFilterService>();

var host = builder.Build();

var ingestor = host.Services.GetRequiredService<DatasetIngestor>();
var searchService = host.Services.GetRequiredService<SearchService>();
var chatClient = host.Services.GetRequiredService<IChatClient>(); // Get the LLM client
var embeddingGenerator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var semanticCache = host.Services.GetRequiredService<SemanticCacheService>();
var inputFilter = host.Services.GetRequiredService<InputFilterService>();

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

    // 1) Classify the query intent using the router
    var intent = QueryIntent.OnePiece;
    if (filterOptions.Enabled)
    {
        intent = await inputFilter.ClassifyQueryAsync(query);
    }

    if (intent == QueryIntent.General)
    {
        ForegroundColor = ConsoleColor.Blue;
        WriteLine("\n[General Query Route] Bypassing RAG search...");
        ResetColor();

        ForegroundColor = ConsoleColor.Green;
        Write("\n[Answer]: ");

        var responseStream = chatClient.GetStreamingResponseAsync(query);
        await foreach (var update in responseStream)
        {
            Write(update.Text);
        }
        WriteLine();
        ResetColor();
        continue;
    }

    // 2) Generate the query embedding (only for One Piece domain queries)
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

    List<EpisodeRecord> matches;

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

        matches = cachedSources;
    }
    else
    {
        if (cacheOptions.Enabled)
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine("\n[Semantic Cache Miss] Processing query...");
            ResetColor();
        }

        // Retrieve the closest matching context items from Qdrant using the precomputed embedding
        matches = await searchService.SearchAsync(queryVector, limit: 5);

        if (!matches.Any())
        {
            WriteLine("No matching episodes found.");
            continue;
        }

        // 2) Format the vector results into text data for the LLM context window
        var contextData = string.Join("\n", matches.Select(e =>
            $"- Title: {e.Title}, Season: {e.Season}, Episode: {e.EpisodeNumber}, Year: {e.ReleaseYear}, Rating: {e.Rating}\n  Overview: {e.Overview}"));

        // 3) Construct a prompt that forces the LLM to ground its response in your data
        var prompt = $"""
                      You are an expert One Piece assistant. Answer the user's question accurately using ONLY the provided context dataset below. 
                      If the user asks for a specific episode (like the "best" or "highest rated"), evaluate the attributes (like Rating) in the dataset to give a singular definitive answer.

                      Context Dataset:
                      {contextData}

                      User Question: {query}
                      Answer:
                      """;

        ForegroundColor = ConsoleColor.Yellow;
        WriteLine("\nThinking...");
        ResetColor();

        // 4) Stream the finalized single answer
        var responseStream = chatClient.GetStreamingResponseAsync(prompt);

        ForegroundColor = ConsoleColor.Green;
        Write("\n[Answer]: "); // Print the label ONCE before the loop starts

        var sb = new System.Text.StringBuilder();
        await foreach (var update in responseStream)
        {
            Write(update.Text);
            sb.Append(update.Text);
        }

        WriteLine();
        ResetColor();

        var answerText = sb.ToString();

        // Save response to semantic cache
        if (cacheOptions.Enabled)
        {
            await semanticCache.SaveToCacheAsync(query, queryVector, answerText, matches);
        }
    }

    // 5) Print references underneath the answer
    WriteLine("\n--- Sources Used ---");
    foreach (var episode in matches)
    {
        WriteLine($"* {episode.Title} (Rating: {episode.Rating})");
    }
}