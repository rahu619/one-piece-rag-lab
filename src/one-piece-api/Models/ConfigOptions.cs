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

/// <summary>
/// Configuration options for the safety guardrails applied to user input and model output.
/// </summary>
public record SafetyOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether guardrail evaluation runs at all. Disabling is
    /// intended for controlled evaluation runs only, never for interactive use.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum accepted query length in characters. Longer input is rejected
    /// because it usually signals prompt-smuggling or abuse, and it can overrun small context windows.
    /// </summary>
    public int MaxQueryLength { get; set; } = 2000;

    /// <summary>
    /// Gets or sets a value indicating whether prompt-injection patterns (instruction overrides,
    /// jailbreak phrasing) are blocked on input.
    /// </summary>
    public bool BlockPromptInjection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether queries containing personal data (emails, phone
    /// numbers, national IDs, card numbers) are rejected, and whether such data is redacted
    /// from observability traces.
    /// </summary>
    public bool BlockPii { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether harmful requests (weapon synthesis, violence,
    /// self-harm) are blocked on input and withheld on output.
    /// </summary>
    public bool BlockHarmfulContent { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether detected personal data is redacted from trace
    /// files. When false, prompts are logged verbatim — enable only on trusted machines.
    /// </summary>
    public bool RedactPiiInTraces { get; set; } = true;
}

/// <summary>
/// Configuration options for LLM observability: JSONL trace files and pipeline metrics.
/// </summary>
public record ObservabilityOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether tracing and metrics collection runs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the directory receiving trace JSONL files and evaluation reports.
    /// Relative paths resolve against the current working directory.
    /// </summary>
    public string TraceDirectory { get; set; } = "observability";

    /// <summary>
    /// Gets or sets a value indicating whether prompt and completion text is included in trace
    /// events. When false, only metadata (latency, tokens, status) is recorded.
    /// </summary>
    public bool LogPrompts { get; set; } = true;
}