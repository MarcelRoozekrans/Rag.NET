using System.Collections.Concurrent;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;

namespace Rag.NET.Storage;

/// <summary>
/// Thread-safe in-memory store for parent chunk text.
/// Process-scoped, not persisted — rebuilt on re-ingestion (same trade-off as <see cref="Search.InMemoryBm25Index"/>).
/// </summary>
public sealed class InMemoryParentChunkStore : IParentChunkStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public void Add(string documentId, int parentChunkIndex, string text)
    {
        var key = GetParentKey(documentId, parentChunkIndex);
        _store[key] = text;
    }

    public bool TryGet(string documentId, int parentChunkIndex, out string? text)
    {
        var key = GetParentKey(documentId, parentChunkIndex);
        if (_store.TryGetValue(key, out var value))
        {
            text = value;
            return true;
        }

        text = null;
        return false;
    }

    public void Remove(string documentId)
    {
        var prefix = documentId + ":";
        foreach (var key in _store.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                _store.TryRemove(key, out _);
        }
    }

    public static string GetParentKey(string documentId, int parentChunkIndex)
        => ParentChunkKeyHelper.GetParentKey(documentId, parentChunkIndex);

    /// <summary>
    /// Finds which parent chunk contains a child chunk based on start position.
    /// </summary>
    public static int FindParentIndex(IReadOnlyList<(int start, int end)> parentBoundaries, int childStart)
        => ParentChunkKeyHelper.FindParentIndex(parentBoundaries, childStart);
}
