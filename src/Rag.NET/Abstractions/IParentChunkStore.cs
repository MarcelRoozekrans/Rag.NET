namespace Rag.NET.Abstractions;

/// <summary>
/// Store for parent chunk text keyed by (documentId, parentChunkIndex).
/// </summary>
public interface IParentChunkStore : IDisposable, IAsyncDisposable
{
    void Add(string documentId, int parentChunkIndex, string text);
    bool TryGet(string documentId, int parentChunkIndex, out string? text);
    void Remove(string documentId);
}
