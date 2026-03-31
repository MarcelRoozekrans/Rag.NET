namespace Rag.NET.Models;

/// <summary>Reports the completion of a single stage during document ingestion.</summary>
public sealed record IngestionProgress
{
    /// <summary>Gets the pipeline stage that just completed.</summary>
    public required IngestionProgressStage Stage { get; init; }

    /// <summary>Gets the document ID being ingested.</summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// Gets the number of items processed so far, or <see langword="null"/> when not applicable for this stage.
    /// </summary>
    public int? Current { get; init; }

    /// <summary>
    /// Gets the total number of items to process, or <see langword="null"/> when the total is not known for this stage.
    /// </summary>
    public int? Total { get; init; }

    /// <summary>Gets a human-readable message describing what was completed.</summary>
    public required string Message { get; init; }
}
