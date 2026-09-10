using System.Diagnostics;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

/// <summary>
/// Hybrid search. When the registered store implements <see cref="IHybridSearchable"/> and the
/// request configures nothing the native call cannot express (see
/// <see cref="CanDispatchNatively"/>), the store's own server-side hybrid query serves the
/// request in a single backend call. Otherwise dense vector search is fused client-side with
/// BM25 and — when an <see cref="ISparseEmbeddingGenerator"/> is registered and the store
/// implements <see cref="ISparseSearchable"/> — a learned sparse (SPLADE) arm, all merged by
/// weighted Reciprocal Rank Fusion. Which path served a query is observable: the
/// <c>ragnet.retrieve</c> activity gains a <c>retrieval.hybrid.path</c> tag (<c>native</c> or
/// <c>client</c>), and the native path logs <c>ensemble_native_hybrid</c> naming the store.
/// The client path is degraded, never broken: a failed BM25 or sparse arm is logged and the
/// remaining arms are fused; dense-only results are returned when both fail. The native path
/// has no fallback — a failed native hybrid call throws, like a failed dense search.
/// </summary>
[Singleton]
public sealed class EnsembleBehavior : IRetrievalBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Required = false)] public ISparseEmbeddingGenerator? SparseGenerator { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var opts = ctx.Options;

        RefuseIfNativeOnlyCapabilityIsUnreachable(opts);

        if (!opts.UseHybridSearch)
            return await next(ctx, ct).ConfigureAwait(false);

        WarnIfADecoratorHidesNativeHybrid(ctx);

        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
        };

        var queryVector = await QueryVectorResolver.ResolveAsync(opts, ctx.Query, Embedder, ct).ConfigureAwait(false);

        if (VectorStore is IHybridSearchable nativeStore && CanDispatchNatively(opts))
        {
            Activity.Current?.SetTag("retrieval.hybrid.path", "native");
            RagPipelineLog.EnsembleNativeHybrid(ctx.Logger, VectorStore.GetType().Name);
            // The text side searches the raw query (like the BM25 arm below) even when HyDE
            // resolved the dense vector from a hypothesis — lexical matching wants the user's
            // actual terms.
            return await nativeStore.HybridSearchAsync(ctx.Query, queryVector, searchOptions, ct).ConfigureAwait(false);
        }

        Activity.Current?.SetTag("retrieval.hybrid.path", "client");
        return await FuseClientSideAsync(ctx, searchOptions, queryVector, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a request that asks for something only the store's native hybrid query can do, when
    /// this query cannot reach that path. Correct-but-silently-degraded is the failure mode this
    /// library treats as an error (#539).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called before the <see cref="RetrievalOptions.UseHybridSearch"/> early return</b>, not
    /// after: a caller who enabled a native-only capability at registration and left
    /// <see cref="RetrievalOptions.UseHybridSearch"/> unset would otherwise get dense search
    /// forever, silently without that capability — the same defect one layer up.
    /// </para>
    /// <para>
    /// <b>Blank is treated as absent</b>, not as an unnamed capability. The declared value's whole
    /// job is to be quoted into the message, and an empty one renders "is configured for , which
    /// only its native hybrid query performs" — a refusal naming nothing the caller can act on. A
    /// store declaring blank has declared nothing usable, so it takes the ordinary path.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The store declares an <see cref="IHybridSearchable.NativeOnlyCapability"/> and
    /// <see cref="NativeDispatchBlocker"/> names a setting keeping this query off the native path.
    /// </exception>
    private void RefuseIfNativeOnlyCapabilityIsUnreachable(RetrievalOptions opts)
    {
        if (VectorStore is not IHybridSearchable { NativeOnlyCapability: var declared }
            || string.IsNullOrWhiteSpace(declared)
            || NativeDispatchBlocker(opts) is not { } blocker)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{VectorStore.GetType().Name} is configured for {declared}, which only its " +
            $"native hybrid query performs, but this request cannot use that path because " +
            $"{blocker} keeps it on client-side fusion. Client-side fusion would return " +
            $"correct results with {declared} silently absent, so the request is refused " +
            $"rather than downgraded. Either adjust {blocker}, or turn off {declared} on the " +
            "store. Note: registering Rag.NET.Resilience also prevents native hybrid dispatch " +
            "for a different reason and is not detected here — see issue #544.");
    }

    /// <summary>
    /// Warns when the registered store is a decorator that does not forward
    /// <see cref="IHybridSearchable"/>, so native hybrid dispatch is unreachable however the query
    /// is configured (#544).
    /// </summary>
    /// <remarks>
    /// A decorator hides <see cref="IHybridSearchable.NativeOnlyCapability"/> along with the
    /// capability itself, so <see cref="RefuseIfNativeOnlyCapabilityIsUnreachable"/> cannot fire
    /// for it: <see cref="IVectorStoreDecorator"/> deliberately exposes only the inner
    /// <see cref="Type"/> — "so no caller can reach around whatever behaviour the decorator adds" —
    /// and only the instance would know whether the capability is on. Warns rather than throws: a
    /// caller pairing resilience with a hybrid-capable store that declares no native-only
    /// capability gets correct client-side results and has nothing to fix.
    /// </remarks>
    private void WarnIfADecoratorHidesNativeHybrid(RetrievalContext ctx)
    {
        if (VectorStore is not IHybridSearchable
            && VectorStore is IVectorStoreDecorator decorator
            && typeof(IHybridSearchable).IsAssignableFrom(decorator.InnerStoreType))
        {
            RagPipelineLog.NativeHybridHiddenByDecorator(
                ctx.Logger, VectorStore.GetType().Name, decorator.InnerStoreType.Name);
        }
    }

    /// <summary>
    /// The name of the first request setting that keeps this query on the client-side path, or
    /// <see langword="null"/> when the store's native hybrid query can serve it. Native fusion
    /// happens inside the backend, so it cannot apply <see cref="EnsembleOptions"/> weights,
    /// cannot run a sparse (SPLADE) arm, and does not apply <see cref="RetrievalOptions.MinScore"/>
    /// at all — a native implementer's fused score is on its own scale, not the dense arm's
    /// similarity scale. Each of those therefore keeps the client-side path: an
    /// <see cref="RetrievalOptions.EnsembleOptions"/> instance (even default-valued — supplying one
    /// at all expresses weighting intent), a non-zero <see cref="RetrievalOptions.MinScore"/>
    /// (dispatching natively would silently discard it), or a sparse arm that would run
    /// (<see cref="SparseArmWouldRun"/>). <see cref="RetrievalOptions.UseHybridSearch"/> left unset
    /// belongs in the same list and was never named there, because until
    /// <see cref="IHybridSearchable.NativeOnlyCapability"/> existed it had no consequence worth
    /// naming — the query simply never reached this behaviour's hybrid branch.
    /// </summary>
    /// <remarks>
    /// Returns the option's name rather than a <see langword="bool"/> because a store declaring
    /// <see cref="IHybridSearchable.NativeOnlyCapability"/> turns this from a routing decision into
    /// an error, and an error that says "cannot dispatch natively" without saying which of four
    /// settings caused it is not actionable. The order is not arbitrary:
    /// <see cref="RetrievalOptions.UseHybridSearch"/> is tested first because it is the condition a
    /// caller is most likely to have simply forgotten, and reporting a <c>MinScore</c> problem to
    /// someone who never turned hybrid search on would send them to the wrong place.
    /// </remarks>
    private string? NativeDispatchBlocker(RetrievalOptions opts) =>
        !opts.UseHybridSearch ? nameof(RetrievalOptions.UseHybridSearch)
        : opts.EnsembleOptions is not null ? nameof(RetrievalOptions.EnsembleOptions)
        : opts.MinScore is not 0.0 ? nameof(RetrievalOptions.MinScore)
        : SparseArmWouldRun(opts) ? nameof(RetrievalOptions.UseSparseSearch)
        : null;

    /// <summary>
    /// Whether the store's native hybrid query can serve this request without silently doing less
    /// than the caller configured — the negation of <see cref="NativeDispatchBlocker"/>.
    /// </summary>
    private bool CanDispatchNatively(RetrievalOptions opts) => NativeDispatchBlocker(opts) is null;

    /// <summary>
    /// Whether the sparse (SPLADE) arm participates: not disabled per call
    /// (<see cref="RetrievalOptions.UseSparseSearch"/> <see langword="null"/> follows
    /// <see cref="RetrievalOptions.UseHybridSearch"/>, already <see langword="true"/> on this
    /// path), a generator is registered, and the store is sparse-capable.
    /// </summary>
    private bool SparseArmWouldRun(RetrievalOptions opts) =>
        opts.UseSparseSearch != false && SparseGenerator is not null && VectorStore is ISparseSearchable;

    /// <summary>
    /// The client-side ensemble: dense, BM25, and (when <see cref="SparseArmWouldRun"/>) sparse
    /// arms run concurrently and are merged by weighted Reciprocal Rank Fusion.
    /// </summary>
    private async ValueTask<IReadOnlyList<SearchResult>> FuseClientSideAsync(
        RetrievalContext ctx, SearchOptions searchOptions, ReadOnlyMemory<float> queryVector, CancellationToken ct)
    {
        var opts = ctx.Options;
        var ensembleOpts = opts.EnsembleOptions ?? new EnsembleOptions();

        var denseTask = VectorStore.SearchAsync(queryVector, searchOptions, ct);

        Task<IReadOnlyList<SearchResult>?>? sparseTask = null;
        if (SparseArmWouldRun(opts) && VectorStore is ISparseSearchable sparseStore)
            sparseTask = SearchSparseSafeAsync(sparseStore, ctx, searchOptions, ct);

        IReadOnlyList<(TextChunk chunk, double score)>? bm25Hits;
        try
        {
            bm25Hits = Bm25Index.Search(
                ctx.Query, topK: searchOptions.TopK, metadataFilter: searchOptions.MetadataFilter);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.EnsembleBm25Failed(ctx.Logger, ex);
            bm25Hits = null;
        }

        var denseResults = await denseTask.ConfigureAwait(false);
        var sparseResults = sparseTask is null ? null : await sparseTask.ConfigureAwait(false);

        // Dense-only: preserve the store's native scores instead of re-scoring one list by RRF.
        if (bm25Hits is null && sparseResults is null)
            return denseResults;

        var rankings = new List<(IReadOnlyList<SearchResult> Hits, double Weight)>(3)
        {
            (denseResults, ensembleOpts.DenseWeight),
        };
        if (bm25Hits is not null)
            rankings.Add((RrfMerger.ToSearchResults(bm25Hits), ensembleOpts.Bm25Weight));
        if (sparseResults is not null)
            rankings.Add((sparseResults, ensembleOpts.SparseWeight));

        return RrfMerger.MergeMany(rankings, opts.TopK, ensembleOpts.K);
    }

    /// <summary>
    /// Encodes the query and runs the sparse search; any failure other than the caller's own
    /// cancellation is logged and yields <see langword="null"/> so the remaining arms still
    /// serve the request (store-internal timeouts must not kill the composite operation).
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>?> SearchSparseSafeAsync(
        ISparseSearchable sparseStore, RetrievalContext ctx, SearchOptions searchOptions, CancellationToken ct)
    {
        try
        {
            var sparseQuery = await SparseGenerator!.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
            if (sparseQuery.Count == 0)
                return null;

            return await sparseStore.SearchSparseAsync(sparseQuery, searchOptions, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.EnsembleSparseFailed(ctx.Logger, ex);
            return null;
        }
    }
}
