using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Sidecar metadata store tracking documents and chunks ingested by <see cref="Rag.NET.Ingestion.DocumentIngestor"/>.
/// Write methods are called internally; read methods are the public management surface.
/// </summary>
public interface IRagDataManager : IDisposable, IAsyncDisposable
{
    // Write — called internally by DocumentIngestor
    void Add(DocumentMetadata metadata, IReadOnlyList<TextChunk> chunks);
    void Remove(string documentId);

    // Read — public API
    Task<IReadOnlyList<DocumentSummary>> GetDocumentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TextChunk>> GetChunksAsync(string documentId, CancellationToken cancellationToken = default);
    Task<DataManagerStats> GetStatsAsync(CancellationToken cancellationToken = default);

    // Lifecycle
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
