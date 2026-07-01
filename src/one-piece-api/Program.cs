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

// Register the IChatClient for answering queries with Function Invocation enabled
builder.Services.AddSingleton<IChatClient>(sp =>
{
    IChatClient innerClient = new OllamaApiClient(ollamaUri, llmModelId);
    return innerClient.AsBuilder().UseFunctionInvocation().Build();
});

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

// We need a thread-local or dynamic list to capture matched episodes during semantic search executions in the current chat turn
    var activeMatches = new List<EpisodeRecord>();

    // Define the structured and unstructured tools for the Agent
    string ExecuteSqlQuery(string sql)
    {
        ForegroundColor = ConsoleColor.Yellow;
        WriteLine($"\n[Agent Executing Tool: SQL Query]");
        WriteLine($"Query: {sql}");
        ResetColor();
        
        var dbService = host.Services.GetRequiredService<SqliteDatabaseService>();
        return dbService.ExecuteSqlQuery(sql);
    }

    async Task<string> SearchSemanticContext(string query)
    {
        ForegroundColor = ConsoleColor.Yellow;
        WriteLine($"\n[Agent Executing Tool: Semantic Search]");
        WriteLine($"Semantic Query: {query}");
        ResetColor();

        var embeddingResult = await embeddingGenerator.GenerateAsync(query);
        var searchMatches = await searchService.SearchAsync(embeddingResult.Vector, limit: 5);
        
        if (!searchMatches.Any())
        {
            return "No matching episodes found.";
        }

        var sortedMatches = searchMatches.OrderByDescending(e => e.Rating).ToList();
        
        lock (activeMatches)
        {
            activeMatches.AddRange(sortedMatches);
        }

        return string.Join("\n", sortedMatches.Select(e =>
            $"- Title: {e.Title}, Season: {e.Season}, Episode: {e.EpisodeNumber}, Year: {e.ReleaseYear}, Rating: {e.Rating}\n  Overview: {e.Overview}"));
    }

    while (true)
    {
        ForegroundColor = ConsoleColor.Cyan;
        Write("\nEnter search query: ");
        ResetColor();

        string? query = ReadLine();
        if (string.IsNullOrWhiteSpace(query)) break;

        activeMatches.Clear();

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

        // Cache Miss -> Process via LLM Agent
        if (cacheOptions.Enabled)
        {
            ForegroundColor = ConsoleColor.Yellow;
            WriteLine("\n[Semantic Cache Miss] Processing query...");
            ResetColor();
        }

        // Set up agent prompt and options
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "You are a helpful One Piece assistant. You have access to tools to search episodes semantically or query a structured SQLite database for season ratings, episode counts, or release years. If a query requires calculations, groupings, averages, maximums, or minimums, write and execute a SQLite query using ExecuteSqlQuery. If the user asks about storylines, characters, plot points, or what happened, use SearchSemanticContext. If the query does not relate to One Piece or is a simple greeting, answer it directly without calling any tools. For security, only run SELECT SQL queries."),
            new ChatMessage(ChatRole.User, query)
        };

        var options = new ChatOptions
        {
            Tools = new[]
            {
                AIFunctionFactory.Create(SearchSemanticContext, "SearchSemanticContext", "Searches episode overviews semantically to find matches relating to character actions, storylines, plot details, or overviews."),
                AIFunctionFactory.Create(ExecuteSqlQuery, "ExecuteSqlQuery", "Executes a SELECT SQL query against the 'Episodes' SQLite database to answer analytical queries (averages, counts, maximums, minimums, grouping, sorting by rating/season/year). Schema: Episodes(Id INT, Title TEXT, Overview TEXT, Season INT, EpisodeNumber INT, ReleaseYear INT, Rating REAL)")
            }
        };

        ForegroundColor = ConsoleColor.Yellow;
        WriteLine("\nThinking...");
        ResetColor();

        ForegroundColor = ConsoleColor.Green;
        Write("\n[Answer]: ");

        var responseStream = chatClient.GetStreamingResponseAsync(messages, options);
        var sb = new System.Text.StringBuilder();
        await foreach (var update in responseStream)
        {
            Write(update.Text);
            sb.Append(update.Text);
        }
        WriteLine();
        ResetColor();

        var answerText = sb.ToString();

        // Save to semantic cache
        if (cacheOptions.Enabled)
        {
            await semanticCache.SaveToCacheAsync(query, queryVector, answerText, activeMatches.ToList());
        }

        if (activeMatches.Any())
        {
            WriteLine("\n--- Sources Used ---");
            foreach (var episode in activeMatches.DistinctBy(e => e.Id))
            {
                WriteLine($"* {episode.Title} (Rating: {episode.Rating})");
            }
        }
    }