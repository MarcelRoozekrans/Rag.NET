namespace Rag.NET.Models;

public sealed record IngestionResult
{
    public required string DocumentId { get; init; }
    public required int ChunksStored { get; init; }
}
