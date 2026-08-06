namespace Rag.NET.Models;

/// <summary>A chunk paired with its dense embedding, ready for <see cref="Rag.NET.Abstractions.IVectorStore.StoreAsync"/>.</summary>
public sealed record EmbeddedChunk
{
    /// <summary>The embedded chunk.</summary>
    public required TextChunk Chunk { get; init; }

    /// <summary>
    /// The chunk's dense embedding vector. Dimensionality must match the vector store's
    /// configured dimension; a mismatch is the store's concern, not validated here.
    /// </summary>
    public required ReadOnlyMemory<float> Embedding { get; init; }
}
