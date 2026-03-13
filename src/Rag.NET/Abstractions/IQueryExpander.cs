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
    /// An empty list is a valid return value; callers fall back to single-query retrieval in that case.
    /// </summary>
    Task<IReadOnlyList<string>> ExpandAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}
