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
    /// What this store's native hybrid query does that client-side fusion cannot reproduce, or
    /// <see langword="null"/> when client-side fusion is an honest substitute. Phrased as a short
    /// noun phrase, because it is quoted into the error a caller sees — <c>"semantic ranking"</c>,
    /// not <c>"this store supports semantic ranking"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The retrieval pipeline refuses rather than degrades when this is non-null.</b> Native
    /// dispatch is conditional — an <see cref="RetrievalOptions.EnsembleOptions"/>, a non-zero
    /// <see cref="RetrievalOptions.MinScore"/>, a sparse arm that would run, or
    /// <see cref="RetrievalOptions.UseHybridSearch"/> left unset each keep the client-side path.
    /// For every store that exists today that is a fair trade: client-side Reciprocal Rank Fusion
    /// computes the same kind of answer the backend would. For a store that declares something
    /// here, it is not — the caller asked for a capability and would receive correct results with
    /// that capability silently absent, which is the failure mode this library treats as an error
    /// rather than a downgrade (#539).
    /// </para>
    /// <para>
    /// <b>Defaulted to <see langword="null"/> rather than required</b>, for the same reason
    /// <see cref="HybridScoreScale"/> is defaulted: it is correct for every implementer that
    /// exists, and a new member on this interface must not break the ones that do.
    /// </para>
    /// <para>
    /// <b>A general capability, not a per-backend flag.</b> The pipeline must not know what Azure's
    /// semantic ranker is; it must know only that this store would lose something. Anything that
    /// would make the retrieval behaviour name a specific backend belongs behind this member
    /// instead.
    /// </para>
    /// <para>
    /// <b>The probe is on the registered <see cref="IVectorStore"/> instance</b>, so a decorator
    /// that does not forward <see cref="IHybridSearchable"/> hides this declaration along with the
    /// capability itself — see issue #544.
    /// </para>
    /// </remarks>
    string? NativeOnlyCapability => null;

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
