namespace Rag.NET.Models;

public sealed record SearchResult
{
    public required TextChunk Chunk { get; init; }
    public required double Score { get; init; }
}
