using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability for an <see cref="IVectorStore"/> that can combine dense vector search with
/// sparse/keyword (BM25) search in a single backend call. When
/// <see cref="RetrievalOptions.UseHybridSearch"/> is set, the retrieval pipeline probes the
/// registered store for this interface and dispatches to <see cref="HybridSearchAsync"/> —
/// but only when the request configures nothing that native fusion cannot express. A sparse
/// (SPLADE) arm that would run, a supplied <see cref="RetrievalOptions.EnsembleOptions"/>
/// (native fusion cannot apply its weights), or a non-zero
/// <see cref="RetrievalOptions.MinScore"/> (the native path would threshold the store's own
/// fusion-score scale instead of the dense arm's similarity scale) each keep the client-side
/// dense+BM25 Reciprocal Rank Fusion, as does a store that does not implement this interface.
/// The probe is on the registered <see cref="IVectorStore"/> instance itself: a decorator that
/// does not forward this interface hides the capability.
/// </summary>
public interface IHybridSearchable
{
    /// <summary>
    /// Searches using both <paramref name="textQuery"/> (keyword/BM25) and
    /// <paramref name="queryEmbedding"/> (dense), fusing the two result sets internally — unlike
    /// <see cref="IVectorStore.SearchAsync"/>, which is dense-only. Scores are on the backend's
    /// own hybrid-fusion scale, which is not the dense path's similarity scale.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default);
}
