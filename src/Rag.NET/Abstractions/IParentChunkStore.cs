namespace Rag.NET.Abstractions;

/// <summary>
/// Store for parent chunk text keyed by (documentId, parentChunkIndex).
/// </summary>
public interface IParentChunkStore : IDisposable, IAsyncDisposable
{
    /// <summary>Creates or migrates any backing storage needed by the store.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Add(string documentId, int parentChunkIndex, string text);
    bool TryGet(string documentId, int parentChunkIndex, out string? text);
    void Remove(string documentId);

    /// <summary>Removes all entries from the store.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
