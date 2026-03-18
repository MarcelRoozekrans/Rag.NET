namespace Rag.NET.Models;

public sealed record DataManagerStats
{
    public required int DocumentCount   { get; init; }
    public required int TotalChunkCount { get; init; }
}
