using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Keyword index supporting Add, Remove, and BM25-scored Search.
/// </summary>
public interface IBm25Index : IDisposable, IAsyncDisposable
{
    /// <summary>Creates or migrates any backing storage needed by the index.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes a chunk under the given integer document id — an index-internal id distinct from
    /// <see cref="Rag.NET.Models.DocumentId"/>, assigned by the caller.
    /// </summary>
    void Add(int docId, TextChunk chunk);

    /// <summary>Removes every chunk indexed for a document, keyed by its <see cref="Rag.NET.Models.DocumentId"/> string value.</summary>
    void Remove(string documentId);

    /// <summary>
    /// Returns up to <paramref name="topK"/> chunks ranked by BM25 score against
    /// <paramref name="query"/>, best first.
    /// </summary>
    IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK);

    /// <summary>Removes all documents and resets the index.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
