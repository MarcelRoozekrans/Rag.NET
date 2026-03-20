namespace Rag.NET.Models;

public sealed record IngestionResult
{
    public required DocumentId DocumentId { get; init; }
    public required int ChunksStored { get; init; }
}
