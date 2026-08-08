using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

// Register the Ollama-based embedding generator. A dedicated embedding model is used here rather
// than the chat model, whose hidden states make weak retrieval vectors.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OllamaApiClient(ollamaUri, ollamaOptions.EmbeddingModelId));

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

// Pooled so the ingestion loop reuses contexts instead of rebuilding one per batch.
builder.Services.AddPooledDbContextFactory<OnePieceDbContext>(options =>
    options.UseSqlite(OnePieceDbContext.BuildConnectionString(SqliteOpenMode.ReadWriteCreate)));

builder.Services.AddTransient<DatasetIngestor>();
builder.Services.AddTransient<SearchService>();
builder.Services.AddSingleton<SemanticCacheService>();
builder.Services.AddSingleton<SqliteDatabaseService>();

using var host = builder.Build();

var ingestor = host.Services.GetRequiredService<DatasetIngestor>();
var searchService = host.Services.GetRequiredService<SearchService>();
var chatClient = host.Services.GetRequiredService<IChatClient>(); // Get the LLM client
var embeddingGenerator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var semanticCache = host.Services.GetRequiredService<SemanticCacheService>();

// Fail fast on a model/schema mismatch. Otherwise the width only surfaces as an opaque Qdrant
// error part-way through ingestion.
var probeVector = (await embeddingGenerator.GenerateAsync("dimension probe")).Vector;
if (probeVector.Length != EmbeddingSchema.Dimensions)
{
    ForegroundColor = ConsoleColor.Red;
    WriteLine($"Embedding model '{ollamaOptions.EmbeddingModelId}' returns {probeVector.Length} dimensions, " +
              $"but the collections are declared as {EmbeddingSchema.Dimensions}. " +
              $"Update EmbeddingSchema.Dimensions to {probeVector.Length} and re-ingest.");
    ResetColor();
    return 1;
}

// Ensure the cache collection exists
if (cacheOptions.Enabled)
{
    var cacheCollection = host.Services.GetRequiredService<VectorStoreCollection<ulong, CacheRecord>>();
    await cacheCollection.EnsureCollectionExistsAsync();
}

// The router prompt asks for a single word, so cap generation rather than paying for a
// full-length response on every query. Temperature 0 keeps routing deterministic.
var routingChatOptions = new ChatOptions { MaxOutputTokens = 5, Temperature = 0 };

WriteLine("One-piece-api Node Initialized.");

if (ingestionOptions!.RunOnStart && !string.IsNullOrEmpty(ingestionOptions.CsvFilePath))
{
    // Resolve relative paths against the output directory so the app does not depend on
    // the current working directory, which differs between `dotnet run` and a published binary.
    var csvPath = Path.IsPathRooted(ingestionOptions.CsvFilePath)
        ? ingestionOptions.CsvFilePath
        : Path.Combine(AppContext.BaseDirectory, ingestionOptions.CsvFilePath);

    WriteLine($"Ingestion triggered on start via configuration. Target: {csvPath}");
    try
    {
        await ingestor.IngestCsvAsync(csvPath);

        // The episode collection was just rebuilt, so cached answers would cite stale records.
        if (cacheOptions.Enabled)
        {
            await semanticCache.ClearAsync();
            WriteLine("Semantic cache cleared to match the rebuilt dataset.");
        }
    }
    catch (VectorStoreException ex)
    {
        WriteLine($"Vector Store Error: {ex.Message}");
        if (ex.InnerException != null)
        {
            WriteLine($"Inner Exception: {ex.InnerException.Message}");
        }
    }
    catch (IOException ex)
    {
        WriteLine($"Ingestion File Error: {ex.Message}");
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

    // 1) A byte-identical repeat is a direct key fetch, so check it before spending an
    // embedding round trip on the semantic lookup.
    if (cacheOptions.Enabled)
    {
        var (isExactHit, exactAnswer, exactSources) = await semanticCache.GetExactMatchAsync(query);
        if (isExactHit && exactAnswer != null)
        {
            PrintCacheHit(exactAnswer, exactSources, score: null);
            continue;
        }
    }

    // 2) Embed lazily: only the semantic cache lookup and the VECTOR route need a vector,
    // so a cache-disabled SQL or GENERAL query never pays for one.
    ReadOnlyMemory<float>? cachedQueryVector = null;
    async Task<ReadOnlyMemory<float>> GetQueryVectorAsync()
    {
        cachedQueryVector ??= (await embeddingGenerator.GenerateAsync(query)).Vector;
        return cachedQueryVector.Value;
    }

    if (cacheOptions.Enabled)
    {
        var (isHit, cachedAnswer, cachedSources, similarityScore) = await semanticCache.GetCachedResponseAsync(
            query,
            await GetQueryVectorAsync(),
            cacheOptions.SimilarityThreshold);

        if (isHit && cachedAnswer != null)
        {
            PrintCacheHit(cachedAnswer, cachedSources, similarityScore);
            continue;
        }

        ForegroundColor = ConsoleColor.Yellow;
        WriteLine("\n[Semantic Cache Miss] Processing query...");
        ResetColor();
    }

    // 3) Classify the query intent using the LLM router
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

    var routingResponse = await chatClient.GetResponseAsync(classificationPrompt, routingChatOptions);
    var intent = ParseIntent(routingResponse.Text);

    if (intent == "SQL")
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
        var sqlQuery = StripCodeFence(sqlGenResponse.Text);

        ForegroundColor = ConsoleColor.Blue;
        WriteLine($"Executing SQL: {sqlQuery}");
        ResetColor();

        var dbService = host.Services.GetRequiredService<SqliteDatabaseService>();
        var sqlResult = await dbService.ExecuteSqlQueryAsync(sqlQuery);

        var answerPrompt = $"""
            You are an expert One Piece assistant. Answer the user's question accurately using the structured SQLite database results provided below.

            Database Results:
            {sqlResult}

            User Question: {query}
            Answer:
            """;

        var answer = await StreamAnswerAsync(answerPrompt);

        if (cacheOptions.Enabled)
        {
            await semanticCache.SaveToCacheAsync(query, await GetQueryVectorAsync(), answer, []);
        }
    }
    else if (intent == "VECTOR")
    {
        ForegroundColor = ConsoleColor.Yellow;
        WriteLine("\n[Route: Semantic Vector Search]");
        ResetColor();

        var matches = await searchService.SearchAsync(await GetQueryVectorAsync(), limit: 5);
        if (matches.Count == 0)
        {
            WriteLine("No matching episodes found.");
            continue;
        }

        var sortedMatches = matches.OrderByDescending(e => e.Rating).ToList();

        // Invariant formatting: on a comma-decimal locale a rating would otherwise reach the
        // model as "9,1", disagreeing with the SQL route and reading as a list separator.
        var contextData = string.Join("\n", sortedMatches.Select(e => string.Create(
            CultureInfo.InvariantCulture,
            $"- Title: {e.Title}, Season: {e.Season}, Episode: {e.EpisodeNumber}, Year: {e.ReleaseYear}, Rating: {e.Rating}\n  Overview: {e.Overview}")));

        var answerPrompt = $"""
            You are an expert One Piece assistant. Answer the user's question accurately using ONLY the provided context dataset below.

            Context Dataset (ordered from highest rating to lowest rating):
            {contextData}

            User Question: {query}
            Answer:
            """;

        var answer = await StreamAnswerAsync(answerPrompt);

        if (cacheOptions.Enabled)
        {
            await semanticCache.SaveToCacheAsync(query, await GetQueryVectorAsync(), answer, sortedMatches);
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

        var answer = await StreamAnswerAsync(query);

        if (cacheOptions.Enabled)
        {
            await semanticCache.SaveToCacheAsync(query, await GetQueryVectorAsync(), answer, []);
        }
    }
}

return 0;

async Task<string> StreamAnswerAsync(string prompt)
{
    ForegroundColor = ConsoleColor.Green;
    Write("\n[Answer]: ");

    var sb = new System.Text.StringBuilder();
    await foreach (var update in chatClient.GetStreamingResponseAsync(prompt))
    {
        Write(update.Text);
        sb.Append(update.Text);
    }

    WriteLine();
    ResetColor();
    return sb.ToString();
}

static void PrintCacheHit(string answer, List<EpisodeRecord>? sources, double? score)
{
    ForegroundColor = ConsoleColor.Magenta;
    WriteLine(score.HasValue
        ? $"\n[Semantic Cache Hit] (Similarity: {score:F4})"
        : "\n[Cache Hit] (Exact query match)");
    ResetColor();

    ForegroundColor = ConsoleColor.Green;
    Write("\n[Answer]: ");
    Write(answer);
    WriteLine();
    ResetColor();

    if (sources is { Count: > 0 })
    {
        WriteLine("\n--- Sources Used ---");
        foreach (var episode in sources)
        {
            WriteLine($"* {episode.Title} (Rating: {episode.Rating})");
        }
    }
}

static string ParseIntent(string? responseText)
{
    if (string.IsNullOrWhiteSpace(responseText))
    {
        return "GENERAL";
    }

    // Small models echo their prompt, and the router prompt names every category. Searching
    // the whole response with Contains would therefore always match the first-listed category,
    // so match the leading token instead.
    var firstToken = responseText
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?
        .Trim('"', '\'', '.', ':', '*', '`')
        .ToUpperInvariant();

    return firstToken is "SQL" or "VECTOR" ? firstToken : "GENERAL";
}

static string StripCodeFence(string text)
{
    var sql = text.Trim();

    if (sql.StartsWith("```sql", StringComparison.OrdinalIgnoreCase)) sql = sql[6..];
    else if (sql.StartsWith("```")) sql = sql[3..];
    if (sql.EndsWith("```")) sql = sql[..^3];

    return sql.Trim();
}
