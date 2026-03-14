namespace Rag.NET.Models;

public sealed class RerankResult
{
    public required SearchResult SearchResult { get; init; }
    public required double RelevanceScore { get; init; }
}
