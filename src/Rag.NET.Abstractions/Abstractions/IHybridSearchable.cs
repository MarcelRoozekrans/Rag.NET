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
/// <see cref="RetrievalOptions.MinScore"/> (the native path does not apply it at all — see
/// <see cref="HybridScoreScale"/> — so dispatching natively would silently discard the
/// caller's threshold) each keep the client-side dense+BM25 Reciprocal Rank Fusion, as does a
/// store that does not implement this interface.
/// The probe is on the registered <see cref="IVectorStore"/> instance itself: a decorator that
/// does not forward this interface hides the capability.
/// </summary>
public interface IHybridSearchable
{
    /// <summary>
    /// The scale of the scores <see cref="HybridSearchAsync"/> returns. Defaults to
    /// <see cref="ScoreScale.OpaqueRanking"/>, which is what a native hybrid produces: the backend
    /// fuses a keyword ranking with a vector ranking, and a fused rank carries no similarity
    /// meaning — only order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="IScoreScaleAware.ScoreScale"/> because one store instance serves
    /// both paths.</b> The same store answers <see cref="IVectorStore.SearchAsync"/> with a genuine
    /// cosine similarity and this method with a fused score. That interface requires its value to be
    /// constant for the instance's lifetime, so a single property cannot describe both honestly.
    /// </para>
    /// <para>
    /// <b>Defaulted rather than required</b> because it is correct for every implementer that
    /// exists — a store whose hybrid query genuinely returns similarities overrides it and says why.
    /// </para>
    /// <para>
    /// <b>This is a declaration, not a filter.</b> Implementations must not apply
    /// <see cref="Rag.NET.Models.Options.SearchOptions.MinScore"/> to a score on this scale; a
    /// threshold shaped for similarities filters a fused rank arbitrarily.
    /// </para>
    /// </remarks>
    ScoreScale HybridScoreScale => ScoreScale.OpaqueRanking;

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
