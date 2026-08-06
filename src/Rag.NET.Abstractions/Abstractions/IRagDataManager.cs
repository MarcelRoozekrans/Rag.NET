using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Sidecar metadata store tracking documents and chunks ingested by <see cref="Rag.NET.Ingestion.DocumentIngestor"/>.
/// Write methods are called internally; read methods are the public management surface.
/// </summary>
public interface IRagDataManager : IDisposable, IAsyncDisposable
{
    // Write — called internally by DocumentIngestor
    /// <summary>
    /// Records a document's metadata and chunks in the sidecar store, upserting by document id —
    /// a repeat call for the same id replaces its prior metadata and chunks rather than
    /// duplicating them. This is the sidecar record only; it does not write to the vector store.
    /// </summary>
    void Add(DocumentMetadata metadata, IReadOnlyList<TextChunk> chunks);

    /// <summary>
    /// Removes a document and its chunks from the sidecar store. This is the sidecar record only;
    /// it does not delete the document's vectors from the vector store.
    /// </summary>
    void Remove(string documentId);

    // Read — public API
    /// <summary>Lists every document tracked in the sidecar store.</summary>
    Task<IReadOnlyList<DocumentSummary>> GetDocumentsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a document's chunks in ascending <see cref="TextChunk.ChunkIndex"/> order. Returns an
    /// empty list for a document id that is not tracked.
    /// </summary>
    Task<IReadOnlyList<TextChunk>> GetChunksAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Aggregate counts — total tracked documents and total chunks across all of them.</summary>
    Task<DataManagerStats> GetStatsAsync(CancellationToken cancellationToken = default);

    // Lifecycle
    /// <summary>
    /// Prepares the store for use (for example, creating tables on first run). Implementations may
    /// treat this as a cheap no-op once already initialised, so callers can call it defensively
    /// before every use rather than tracking initialisation state themselves.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every document and chunk the sidecar store is tracking.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
