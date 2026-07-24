namespace Rag.NET.Models;

public sealed record TextChunk
{
    public required string Text { get; init; }
    public required DocumentId DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public int StartPosition { get; init; }
    public int EndPosition { get; init; }

    /// <summary>
    /// Optional precomputed embedding (e.g. from late chunking). When set,
    /// <c>EmbeddingBehavior</c> uses it verbatim instead of re-embedding the chunk text.
    /// </summary>
    public ReadOnlyMemory<float>? Embedding { get; init; }

    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
