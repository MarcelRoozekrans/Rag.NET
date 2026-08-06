using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability for an <see cref="IVectorStore"/> that can combine dense vector search with
/// sparse/keyword (BM25) search in a single backend call. The retrieval pipeline probes for this
/// interface and uses it only when <see cref="RetrievalOptions.UseHybridSearch"/> is requested;
/// a store that does not implement it falls back to dense-only search.
/// </summary>
public interface IHybridSearchable
{
    /// <summary>
    /// Searches using both <paramref name="textQuery"/> (keyword/BM25) and
    /// <paramref name="queryEmbedding"/> (dense), fusing the two result sets internally — unlike
    /// <see cref="IVectorStore.SearchAsync"/>, which is dense-only.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);
}
