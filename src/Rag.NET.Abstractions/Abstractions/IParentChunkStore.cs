namespace Rag.NET.Abstractions;

/// <summary>
/// Store for parent chunk text keyed by (documentId, parentChunkIndex).
/// </summary>
public interface IParentChunkStore : IDisposable, IAsyncDisposable
{
    /// <summary>Creates or migrates any backing storage needed by the store.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a parent chunk's text, upserting by <c>(documentId, parentChunkIndex)</c> — a repeat call for the same key replaces its text.</summary>
    void Add(string documentId, int parentChunkIndex, string text);

    /// <summary>Looks up a parent chunk's text. Returns <see langword="false"/>, with <paramref name="text"/> set to <see langword="null"/>, when no entry exists for the key.</summary>
    bool TryGet(string documentId, int parentChunkIndex, out string? text);

    /// <summary>Removes every parent chunk stored for a document.</summary>
    void Remove(string documentId);

    /// <summary>Removes all entries from the store.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
