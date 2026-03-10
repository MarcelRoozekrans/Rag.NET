namespace Rag.NET.Models;

public sealed record RagResponse
{
    public required string Answer { get; init; }
    public required IReadOnlyList<SearchResult> Sources { get; init; }
}
