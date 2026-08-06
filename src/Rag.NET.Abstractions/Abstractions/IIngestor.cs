using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Abstractions;

/// <summary>
/// Parses, chunks, embeds, and stores documents.
/// Implementations may compose as decorators to add pre/post-processing.
/// </summary>
public interface IIngestor
{
    /// <summary>
    /// Parses, chunks, embeds, and stores a document — a full round trip through every
    /// registered ingestion behavior, ending with vectors in the store. Reports progress through
    /// <paramref name="progress"/> when supplied, in addition to the final result.
    /// </summary>
    Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a document's vectors, BM25 entries, sidecar record, and (unlike
    /// <see cref="Rag.NET.Models.Options.IngestionOptions.Overwrite"/>) its parent chunks and
    /// embedding version stamp, wherever those optional stores are registered.
    /// </summary>
    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
