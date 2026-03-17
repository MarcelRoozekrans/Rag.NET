using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Keyword index supporting Add, Remove, and BM25-scored Search.
/// </summary>
public interface IBm25Index : IDisposable
{
    void Add(int docId, TextChunk chunk);
    void Remove(string documentId);
    IReadOnlyList<(TextChunk chunk, double score)> Search(string query, int topK);
}
