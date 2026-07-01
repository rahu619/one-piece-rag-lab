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
    /// Gets or sets the model ID for the Ollama API client. This specifies which model to use for generating embeddings and other tasks. 
    /// The model ID should correspond to a valid model available in the Ollama service.
    /// </summary>
    public required string ModelId { get; set; }
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

/// <summary>
/// Configuration options for the semantic input filter.
/// </summary>
public record InputFilterOptions
{
    public bool Enabled { get; set; } = true;
    public string ClassificationPrompt { get; set; } = """
        You are a query router. Classify the user query into exactly one of two categories:
        - "ONE_PIECE" (if the query is asking about the anime/manga One Piece, episodes, characters, plot, ratings, etc.)
        - "GENERAL" (if the query is a greeting, general chitchat, help request, or a general knowledge/coding/math/physics/geography question not about One Piece)

        Respond with exactly one word: either "ONE_PIECE" or "GENERAL". Do not write anything else.

        Query: {query}
        Category:
        """;
}