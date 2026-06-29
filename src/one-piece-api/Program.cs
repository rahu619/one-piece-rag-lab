using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OllamaSharp;
using OnePieceApi.Ingestion;
using OnePieceApi.Models;
using OnePieceApi.Retrieval;
using Qdrant.Client;

using static System.Console;

var builder = Host.CreateApplicationBuilder(args);


// Register the OpenAI-based generator
// builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
// {
//     var config = sp.GetRequiredService<IConfiguration>();
//     var key = config["OpenAI:ApiKey"] ?? "mock-key-for-local-dev";

//     var client = new OpenAIClient(new ApiKeyCredential(key));

//     return client
//         .GetEmbeddingClient("text-embedding-3-small")
//         .AsIEmbeddingGenerator();
// });

// Register the Ollama-based generator
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new OllamaApiClient(new Uri("http://ollama:11434"), "qwen2.5-coder:1.5b"));

// Get the host from environment variables (will be "qdrant" in Docker) 
// or default to "localhost" if running locally.
var qdrantHost = Environment.GetEnvironmentVariable("QDRANT_HOST") ?? "localhost";    

builder.Services.AddSingleton(new QdrantClient(qdrantHost, 6334));

builder.Services.AddSingleton<VectorStore>(sp =>
{
    var client = sp.GetRequiredService<QdrantClient>();
    return new QdrantVectorStore(client, true);
});

builder.Services.AddSingleton<VectorStoreCollection<ulong, EpisodeRecord>>(sp =>
{
    var store = sp.GetRequiredService<VectorStore>();
    return store.GetCollection<ulong, EpisodeRecord>("one_piece_episodes");
});

builder.Services.AddTransient<DatasetIngestor>();
builder.Services.AddTransient<SearchService>();

var host = builder.Build();

var ingestor = host.Services.GetRequiredService<DatasetIngestor>();
var searchService = host.Services.GetRequiredService<SearchService>();

WriteLine("🏴‍☠️ one-piece-api Node Initialized.");
WriteLine("Uncomment the IngestCsvAsync call in Program.cs if running for the first time.");

try
{
    await ingestor.IngestCsvAsync("./data/one_piece_episodes.csv");
}
catch (VectorStoreException ex)
{
    WriteLine($"Vector Store Error: {ex.Message}");
    if (ex.InnerException != null)
    {
        WriteLine($"Inner Exception: {ex.InnerException.Message}");
    }
}

while (true)
{
    ForegroundColor = ConsoleColor.Cyan;
    Write("\nEnter search query: ");
    ResetColor();

    string? query = ReadLine();
    if (string.IsNullOrWhiteSpace(query)) break;

    var matches = await searchService.SearchAsync(query);

    foreach (var episode in matches)
    {
        WriteLine($"\n[Match] {episode.Title} (Arc: {episode.Season}, Episode: {episode.EpisodeNumber}, Year: {episode.ReleaseYear}, Rating: {episode.Rating})");
        WriteLine($"> {episode.Overview}");
    }
}