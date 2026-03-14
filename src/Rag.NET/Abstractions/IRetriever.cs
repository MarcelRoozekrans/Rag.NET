using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Retrieves semantically relevant chunks for a given query.
/// Implementations may compose as decorators to add post-retrieval processing.
/// </summary>
public interface IRetriever
{
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);
}
