namespace Rag.NET.Models;

public sealed record RagStreamingUpdate
{
    public string? TextDelta { get; init; }
    public IReadOnlyList<SearchResult>? Sources { get; init; }
}
