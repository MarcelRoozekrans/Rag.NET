using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Resilience;

/// <summary>
/// Hybrid-capable <see cref="ResilientVectorStore"/>: serves <see cref="IHybridSearchable"/> by
/// forwarding the native hybrid query through the same resilience pipeline as the dense ones.
/// </summary>
/// <remarks>
/// <para>
/// Split from the dense-only decorator, the same shape as <see cref="ResilientSparseVectorStore"/>,
/// so the capability probe stays honest: decorating a store without a native hybrid must not make
/// <c>store is IHybridSearchable</c> start returning <see langword="true"/>. Instantiate via
/// <see cref="ResilientVectorStore.Create"/>, which picks the right variant.
/// </para>
/// <para>
/// <b>Every member is forwarded, including the two defaulted ones, and that is not incidental.</b>
/// <see cref="IHybridSearchable.NativeOnlyCapability"/> defaults to <see langword="null"/> and
/// <see cref="IHybridSearchable.HybridScoreScale"/> to <see cref="ScoreScale.OpaqueRanking"/>, so a
/// variant forwarding only <see cref="HybridSearchAsync"/> would compile, pass the probe, dispatch
/// natively — and answer for the backend on the rest. Leaving <c>NativeOnlyCapability</c> at its
/// default is the sharp case: the retrieval pipeline's refusal for a request that cannot reach the
/// native path would never fire under resilience, restoring the capability while breaking the guard
/// on it. That is issue #544 one level in.
/// </para>
/// </remarks>
public sealed class ResilientHybridVectorStore : ResilientVectorStore, IHybridSearchable
{
    private readonly IHybridSearchable _hybrid;

    /// <summary>Creates a hybrid-forwarding decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store to decorate. Must implement <see cref="IHybridSearchable"/>.</param>
    /// <param name="pipeline">The resilience pipeline every call is executed through.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> is not <see cref="IHybridSearchable"/>.</exception>
    public ResilientHybridVectorStore(IVectorStore inner, ResiliencePipeline pipeline)
        : base(inner, pipeline) =>
        _hybrid = inner as IHybridSearchable ?? throw new ArgumentException(
            "ResilientHybridVectorStore requires an IHybridSearchable inner store. " +
            "Use ResilientVectorStore.Create, which selects the decorator matching the inner store's capabilities.",
            nameof(inner));

    /// <inheritdoc/>
    /// <remarks>Delegated, never defaulted — see the class remarks.</remarks>
    public string? NativeOnlyCapability => _hybrid.NativeOnlyCapability;

    /// <inheritdoc/>
    /// <remarks>Delegated, never defaulted — see the class remarks.</remarks>
    public ScoreScale HybridScoreScale => _hybrid.HybridScoreScale;

    /// <inheritdoc/>
    /// <remarks>
    /// Retried, symmetric with <see cref="IVectorStore.SearchAsync"/> and with
    /// <see cref="ResilientSparseVectorStore"/>: a native hybrid query is a read against the same
    /// backend, with the same transient failure modes, and idempotent in the same way.
    /// </remarks>
    public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) =>
                await state.Hybrid.HybridSearchAsync(state.TextQuery, state.QueryEmbedding, state.Options, ct)
                    .ConfigureAwait(false),
            (Hybrid: _hybrid, TextQuery: textQuery, QueryEmbedding: queryEmbedding, Options: options),
            cancellationToken).AsTask();
}
