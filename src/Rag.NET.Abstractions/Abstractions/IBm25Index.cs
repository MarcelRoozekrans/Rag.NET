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
    /// <paramref name="query"/>, best first, restricted to chunks satisfying
    /// <paramref name="metadataFilter"/>.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="topK">The maximum number of chunks to return.</param>
    /// <param name="metadataFilter">
    /// Required metadata pairs, or <see langword="null"/> for no filtering. Implementations MUST
    /// apply this via <see cref="MetadataFilterMatcher.Matches"/> (or semantics identical to it)
    /// and MUST apply it <b>before</b> truncating to <paramref name="topK"/>, so the caller
    /// receives the best <i>eligible</i> chunks rather than the best chunks minus the ineligible
    /// ones.
    /// </param>
    /// <returns>Matching chunks with their BM25 scores, best first.</returns>
    IReadOnlyList<(TextChunk chunk, double score)> Search(
        string query, int topK, IDictionary<string, MetadataValue>? metadataFilter = null);

    /// <summary>Removes all documents and resets the index.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
