using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Keyword index supporting Add, Remove, and BM25-scored Search.
/// </summary>
public interface IBm25Index : IDisposable, IAsyncDisposable
{
    /// <summary>Creates or migrates any backing storage needed by the index.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Add(int docId, TextChunk chunk);
    void Remove(string documentId);
    IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK);

    /// <summary>Removes all documents and resets the index.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
