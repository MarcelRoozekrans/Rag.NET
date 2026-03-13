namespace Rag.NET.Abstractions;

/// <summary>
/// Expands a single query into multiple semantically equivalent variants
/// to broaden retrieval recall.
/// </summary>
public interface IQueryExpander
{
    /// <summary>
    /// Generates <paramref name="count"/> alternative phrasings of <paramref name="query"/>.
    /// Implementations may return fewer than <paramref name="count"/> items;
    /// callers must handle partial results.
    /// </summary>
    Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}
