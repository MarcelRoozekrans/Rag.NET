using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Retrieves web search results for a given query.
/// Used by CRAG (<see cref="Rag.NET.Models.Options.RetrievalOptions.UseCrag"/>) as a fallback
/// when post-retrieval relevance scoring drops below threshold.
/// </summary>
public interface IWebSearch
{
    /// <summary>
    /// Searches the web for documents relevant to <paramref name="query"/>
    /// and returns up to <paramref name="topK"/> results as <see cref="SearchResult"/> instances.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default);
}
