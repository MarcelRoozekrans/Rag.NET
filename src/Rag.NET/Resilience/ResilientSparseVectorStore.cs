using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Resilience;

/// <summary>
/// Sparse-capable <see cref="ResilientVectorStore"/>: serves <see cref="ISparseSearchable"/>
/// by forwarding the sparse operations through the same resilience pipeline as the dense ones.
/// </summary>
/// <remarks>
/// Split from the dense-only decorator (the same shape as
/// <c>QdrantSparseVectorStore</c>/<c>PineconeSparseVectorStore</c>) so the capability probe
/// stays honest: decorating a dense-only store must not make
/// <c>store is ISparseSearchable</c> start returning <see langword="true"/>. Instantiate via
/// <see cref="ResilientVectorStore.Create"/>, which picks the right variant.
/// </remarks>
public sealed class ResilientSparseVectorStore : ResilientVectorStore, ISparseSearchable
{
    private readonly ISparseSearchable _sparse;

    /// <summary>Creates a sparse-forwarding decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store to decorate. Must implement <see cref="ISparseSearchable"/>.</param>
    /// <param name="pipeline">The resilience pipeline every call is executed through.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> is not <see cref="ISparseSearchable"/>.</exception>
    public ResilientSparseVectorStore(IVectorStore inner, ResiliencePipeline pipeline)
        : base(inner, pipeline) =>
        _sparse = inner as ISparseSearchable ?? throw new ArgumentException(
            "ResilientSparseVectorStore requires an ISparseSearchable inner store. " +
            "Use ResilientVectorStore.Create, which selects the decorator matching the inner store's capabilities.",
            nameof(inner));

    /// <inheritdoc/>
    public Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) => await state.Sparse.StoreSparseAsync(state.Items, ct).ConfigureAwait(false),
            (Sparse: _sparse, Items: items),
            cancellationToken).AsTask();

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) =>
                await state.Sparse.SearchSparseAsync(state.Query, state.Options, ct).ConfigureAwait(false),
            (Sparse: _sparse, Query: query, Options: options),
            cancellationToken).AsTask();
}
