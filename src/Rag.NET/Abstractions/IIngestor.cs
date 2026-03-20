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
    Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string documentId, CancellationToken cancellationToken = default);
}
