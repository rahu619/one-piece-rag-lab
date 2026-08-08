namespace OnePieceApi.Models;

/// <summary>
/// Vector width shared by every stored collection.
/// </summary>
public static class EmbeddingSchema
{
    /// <summary>
    /// Dimensions produced by the configured embedding model. Vector store attributes need a
    /// compile-time constant, so this is the single place the width is declared.
    /// Changing it invalidates every existing Qdrant collection and requires a re-ingest.
    /// </summary>
    public const int Dimensions = 768;
}
