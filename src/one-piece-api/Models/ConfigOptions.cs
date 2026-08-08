namespace OnePieceApi.Config;

/// <summary>
/// Configuration options for the Ollama API client, including the base URL and model ID.
/// </summary>
public record OllamaOptions
{
    /// <summary>
    /// Gets or sets the base URL for the Ollama API client. This is the endpoint where the Ollama service is hosted, 
    /// and it is used to generate embeddings and perform other operations.
    /// </summary>
    public required string BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the model ID used for chat completion, routing, and SQL generation.
    /// The model ID should correspond to a valid model available in the Ollama service.
    /// </summary>
    public required string ModelId { get; set; }

    /// <summary>
    /// Gets or sets the model ID used for embeddings. A dedicated embedding model retrieves far
    /// better than a chat model's hidden states, so this is kept separate from <see cref="ModelId"/>.
    /// Changing it changes the vector dimensions, which invalidates every existing Qdrant collection.
    /// </summary>
    public required string EmbeddingModelId { get; set; }
}

/// <summary>
/// Configuration options for the ingestion process, including whether to run ingestion on startup and the path to the CSV file containing episode data.
/// </summary>
public record IngestionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the ingestion process should run automatically when the application starts.
    /// </summary>
    public bool RunOnStart { get; set; } 

    /// <summary>
    /// Gets or sets the file path to the CSV file containing episode data to be ingested. 
    /// This path should point to a valid CSV file that contains the necessary data for ingestion into the vector store.
    /// </summary>
    public string? CsvFilePath { get; set; }
}

/// <summary>
/// Configuration options for the semantic cache.
/// </summary>
public record SemanticCacheOptions
{
    public bool Enabled { get; set; } = true;
    public double SimilarityThreshold { get; set; } = 0.95;
    public string CollectionName { get; set; } = "one_piece_cache";
}