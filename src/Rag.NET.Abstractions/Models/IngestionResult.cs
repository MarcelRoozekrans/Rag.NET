namespace Rag.NET.Models;

/// <summary>Outcome of a single <c>IIngestor.IngestAsync</c> call.</summary>
public sealed record IngestionResult
{
    /// <summary>The ingested document's identifier.</summary>
    public required DocumentId DocumentId { get; init; }

    /// <summary>Number of chunks the vector store now holds for this document.</summary>
    public required int ChunksStored { get; init; }
}
