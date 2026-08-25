using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OllamaSharp;
using OnePieceApi.Config;
using OnePieceApi.Ingestion;
using OnePieceApi.Models;
using OnePieceApi.Observability;
using OnePieceApi.Pipeline;
using OnePieceApi.Retrieval;
using OnePieceApi.Safety;
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

// Bind and register options
var cacheOptions = builder.Configuration.GetSection("SemanticCache").Get<SemanticCacheOptions>() ?? new SemanticCacheOptions();
builder.Services.AddSingleton(cacheOptions);

var safetyOptions = builder.Configuration.GetSection("Safety").Get<SafetyOptions>() ?? new SafetyOptions();
builder.Services.AddSingleton(safetyOptions);

var observabilityOptions = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new ObservabilityOptions();
builder.Services.AddSingleton(observabilityOptions);

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

// Safety guardrails
builder.Services.AddSingleton<InputGuardrails>();
builder.Services.AddSingleton<OutputGuardrails>();

// Observability
builder.Services.AddSingleton<TraceStore>();
builder.Services.AddSingleton<PipelineMetrics>();
builder.Services.AddSingleton<LlmInstrumentation>();

// The query pipeline itself
builder.Services.AddSingleton<QueryEngine>();

using var host = builder.Build();

var ingestor = host.Services.GetRequiredService<DatasetIngestor>();
var embeddingGenerator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var semanticCache = host.Services.GetRequiredService<SemanticCacheService>();
var queryEngine = host.Services.GetRequiredService<QueryEngine>();
var metrics = host.Services.GetRequiredService<PipelineMetrics>();

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

WriteLine("One-piece-api Node Initialized.");
WriteLine("Commands: /help for help, /stats for observability metrics.");

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

    if (query.Trim() is "/quit" or "/exit")
    {
        break;
    }

    if (query.Trim() == "/help")
    {
        PrintHelp();
        continue;
    }

    if (query.Trim() == "/stats")
    {
        WriteLine();
        WriteLine(metrics.RenderReport());
        continue;
    }

    var tokenProgress = new Progress<string>(token => Write(token));

    var result = await queryEngine.ExecuteAsync(
        query,
        tokenProgress,
        onStage: evt =>
        {
            switch (evt.Stage)
            {
                case PipelineStage.Routing:
                    ForegroundColor = ConsoleColor.Yellow;
                    WriteLine("Routing query...");
                    ResetColor();
                    break;

                case PipelineStage.SemanticCacheMiss:
                    ForegroundColor = ConsoleColor.Yellow;
                    WriteLine("\n[Semantic Cache Miss] Processing query...");
                    ResetColor();
                    break;

                case PipelineStage.RouteSql:
                    ForegroundColor = ConsoleColor.Yellow;
                    WriteLine("\n[Route: SQL Database Query]");
                    ResetColor();
                    break;

                case PipelineStage.RouteVector:
                    ForegroundColor = ConsoleColor.Yellow;
                    WriteLine("\n[Route: Semantic Vector Search]");
                    ResetColor();
                    break;

                case PipelineStage.RouteGeneral:
                    ForegroundColor = ConsoleColor.Yellow;
                    WriteLine("\n[Route: General Conversation]");
                    ResetColor();
                    break;

                case PipelineStage.SqlGenerated:
                    ForegroundColor = ConsoleColor.Blue;
                    WriteLine($"Executing SQL: {evt.Detail}");
                    ResetColor();
                    break;

                case PipelineStage.AnswerStart:
                    ForegroundColor = ConsoleColor.Green;
                    Write("\n[Answer]: ");
                    break;
            }
        });

    switch (result.Outcome)
    {
        case QueryOutcome.Blocked:
            ForegroundColor = ConsoleColor.Red;
            WriteLine("\n[Blocked by Safety Guardrail]");
            WriteLine(result.Answer);
            ResetColor();
            break;

        case QueryOutcome.Refused:
            ForegroundColor = ConsoleColor.Red;
            WriteLine("\n[Answer Withheld by Output Guardrail]");
            WriteLine(result.Answer);
            ResetColor();
            break;

        case QueryOutcome.NoMatches:
            WriteLine(result.Answer);
            break;

        case QueryOutcome.Answered when result.Cache == CacheOutcome.ExactHit:
            PrintCacheHit(result.Answer, result.Sources.ToList(), score: null);
            break;

        case QueryOutcome.Answered when result.Cache == CacheOutcome.SemanticHit:
            PrintCacheHit(result.Answer, result.Sources.ToList(), result.CacheSimilarity);
            break;

        case QueryOutcome.Answered:
            // The answer was already streamed by the token progress callback.
            WriteLine();
            ResetColor();

            if (result.Sources.Count > 0)
            {
                WriteLine("\n--- Sources Used ---");
                foreach (var episode in result.Sources)
                {
                    WriteLine($"* {episode.Title} (Rating: {episode.Rating})");
                }
            }
            break;
    }

    ForegroundColor = ConsoleColor.DarkGray;
    WriteLine($"— trace: {result.TraceId}, latency: {result.Elapsed.TotalMilliseconds:F0} ms");
    ResetColor();
}

return 0;

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

static void PrintHelp()
{
    WriteLine("""

        One Piece RAG assistant commands:
          <query>    Ask about One Piece episodes, ratings, seasons, or storylines
          /stats     Show observability metrics for this session (routes, cache, guardrails, tokens)
          /help      Show this help
          /quit      Exit

        Safety: queries are screened for prompt injection, personal data, and harmful
        content before they reach the model, and answers are screened before they are
        shown. See docs/ai-safety.md for the full policy.
        Traces: every query and LLM call is written to the observability directory.
        """);
}
