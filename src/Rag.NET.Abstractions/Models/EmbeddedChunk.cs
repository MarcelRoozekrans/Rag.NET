namespace Rag.NET.Models;

public sealed record EmbeddedChunk
{
    public required TextChunk Chunk { get; init; }
    public required ReadOnlyMemory<float> Embedding { get; init; }
}
