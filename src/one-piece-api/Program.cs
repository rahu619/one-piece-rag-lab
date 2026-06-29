using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using OpenAI;
using OnePieceApi.Ingestion;
using OnePieceApi.Models;
using OnePieceApi.Retrieval;
using Qdrant.Client;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// string openAiKey = builder.Configuration["OpenAI:ApiKey"] ?? "mock-key-for-local-dev";
// var openAiClient = new OpenAIClient(new ApiKeyCredential(openAiKey));
// var embeddingClient = openAiClient.GetEmbeddingClient("text-embedding-3-small");
// builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => embeddingClient.AsIEmbeddingGenerator());

// TODO: Switch to Ollama later
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var key = config["OpenAI:ApiKey"] ?? "mock-key-for-local-dev";

    var client = new OpenAIClient(new ApiKeyCredential(key));

    return client
        .GetEmbeddingClient("text-embedding-3-small")
        .AsIEmbeddingGenerator();
});


builder.Services.AddSingleton(new QdrantClient("localhost", 6334));

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

Console.WriteLine("🏴‍☠️ one-piece-api Node Initialized.");
Console.WriteLine("Uncomment the IngestCsvAsync call in Program.cs if running for the first time.");

// await ingestor.IngestCsvAsync("./Data/one_piece.csv");

while (true)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("\nEnter lore search query: ");
    Console.ResetColor();

    string? query = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(query)) break;

    var matches = await searchService.SearchAsync(query);

    foreach (var episode in matches)
    {
        Console.WriteLine($"\n[Match] {episode.Title} (Arc: {episode.Arc})");
        Console.WriteLine($"> {episode.Overview}");
    }
}