namespace Rag.NET.Abstractions;

/// <summary>
/// In-memory index of tag value embeddings used by <c>TagRetriever</c> for automatic
/// metadata filter injection. Populated during ingestion by <c>TagIngestionBehavior</c>.
/// </summary>
public interface ITagIndex : IDisposable, IAsyncDisposable
{
    /// <summary>Returns true if <paramref name="key"/>+<paramref name="value"/> is already indexed.</summary>
    bool Contains(string key, string value);

    /// <summary>
    /// Stores the embedding for a tag key-value pair. No-op when already present.
    /// Thread-safe.
    /// </summary>
    void Add(string key, string value, ReadOnlyMemory<float> embedding);

    /// <summary>
    /// Returns all indexed (key, value) pairs whose cosine similarity to
    /// <paramref name="queryEmbedding"/> is at least <paramref name="minScore"/>,
    /// ordered by score descending. Thread-safe.
    /// </summary>
    /// <remarks>
    /// <c>TopK</c> (max keys to inject per retrieval call) is enforced by
    /// <see cref="Rag.NET.Retrieval.TagRetriever"/> after calling this method,
    /// not by the index itself. The index returns all entries above <paramref name="minScore"/>.
    /// </remarks>
    IReadOnlyList<(string Key, string Value, double Score)> Search(
        ReadOnlyMemory<float> queryEmbedding, double minScore);
}
