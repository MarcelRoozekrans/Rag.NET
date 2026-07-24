namespace Rag.NET.Models;

/// <summary>
/// One embedding version stamp: which model (and dimension) produced the stored vectors
/// of a document, and when.
/// </summary>
public sealed record EmbeddingVersionStamp
{
    /// <summary>The stamped document.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Resolved embedding model identity (e.g. <c>"openai/text-embedding-3-small"</c>).</summary>
    public required string ModelId { get; init; }

    /// <summary>Dimension of the stored dense vectors.</summary>
    public required int Dimension { get; init; }

    /// <summary>When the stamp was written (UTC).</summary>
    public required DateTimeOffset EmbeddedAt { get; init; }
}
