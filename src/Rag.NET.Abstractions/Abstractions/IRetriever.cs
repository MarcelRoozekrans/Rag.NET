using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Abstractions;

/// <summary>
/// Retrieves semantically relevant chunks for a given query.
/// Implementations may compose as decorators to add post-retrieval processing.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Runs the full retrieval pipeline for a query — whichever combination of query expansion,
    /// vector search, reranking, and post-retrieval filtering is configured. Fails with
    /// <see cref="RagError.ValidationFailed"/> before any retrieval work starts if
    /// <paramref name="options"/> carries an out-of-range value (for example a non-positive
    /// <c>TopK</c>).
    /// </summary>
    Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default);
}
