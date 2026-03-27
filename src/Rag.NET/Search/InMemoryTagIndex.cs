using Rag.NET.Abstractions;

namespace Rag.NET.Search;

/// <summary>
/// Thread-safe in-memory store of tag value embeddings.
/// Deduplicates by (key, value) — second Add for the same pair is a no-op.
/// </summary>
public sealed class InMemoryTagIndex : ITagIndex
{
    private readonly Dictionary<(string Key, string Value), float[]> _entries = [];
    private readonly ReaderWriterLockSlim _lock = new();

    public bool Contains(string key, string value)
    {
        _lock.EnterReadLock();
        try   { return _entries.ContainsKey((key, value)); }
        finally { _lock.ExitReadLock(); }
    }

    public void Add(string key, string value, ReadOnlyMemory<float> embedding)
    {
        _lock.EnterWriteLock();
        try   { _entries.TryAdd((key, value), embedding.ToArray()); }
        finally { _lock.ExitWriteLock(); }
    }

    public IReadOnlyList<(string Key, string Value, double Score)> Search(
        ReadOnlyMemory<float> queryEmbedding, double minScore)
    {
        _lock.EnterReadLock();
        try
        {
            var results = new List<(string, string, double)>();
            var q = queryEmbedding.Span;
            foreach (var ((key, value), vec) in _entries)
            {
                var score = CosineSimilarity(q, vec);
                if (score >= minScore)
                    results.Add((key, value, score));
            }
            results.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
