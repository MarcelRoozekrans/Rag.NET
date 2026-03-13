namespace Rag.NET.Models;

public sealed record IngestionProgress
{
    public required IngestionProgressStage Stage { get; init; }
    public required string DocumentId { get; init; }
    public int? Current { get; init; }
    public int? Total { get; init; }
    public required string Message { get; init; }
}
