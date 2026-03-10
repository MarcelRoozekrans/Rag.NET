namespace Rag.NET.Models;

public sealed record TextChunk
{
    public required string Text { get; init; }
    public required string DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    public int StartPosition { get; init; }
    public int EndPosition { get; init; }
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
