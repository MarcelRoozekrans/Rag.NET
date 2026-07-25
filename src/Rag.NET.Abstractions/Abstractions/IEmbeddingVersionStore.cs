using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Tracks which embedding model produced the stored vectors of each document, enabling
/// stale-document detection after an embedding-model switch. Stamped by the ingestion
/// pipeline after a successful store; consumed by <c>ReindexStaleAsync</c>.
/// </summary>
public interface IEmbeddingVersionStore
{
    /// <summary>Prepares the underlying storage (idempotent).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records (or replaces) the embedding version stamp for <paramref name="documentId"/>.
    /// </summary>
    /// <param name="documentId">The document whose vectors were stored.</param>
    /// <param name="modelId">Resolved embedding model identity (e.g. <c>"openai/text-embedding-3-small"</c>).</param>
    /// <param name="dimension">Dimension of the stored dense vectors.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task SetAsync(string documentId, string modelId, int dimension, CancellationToken cancellationToken = default);

    /// <summary>Returns every stored stamp.</summary>
    Task<IReadOnlyList<EmbeddingVersionStamp>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the stamp for <paramref name="documentId"/> (no-op when absent).</summary>
    Task RemoveAsync(string documentId, CancellationToken cancellationToken = default);
}
