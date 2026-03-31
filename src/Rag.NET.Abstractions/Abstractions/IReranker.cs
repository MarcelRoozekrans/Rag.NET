using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Rescores search results using a cross-encoder model for higher precision ranking.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// Reranks <paramref name="results"/> by computing cross-encoder relevance scores
    /// for each (query, passage) pair.
    /// </summary>
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default);
}
