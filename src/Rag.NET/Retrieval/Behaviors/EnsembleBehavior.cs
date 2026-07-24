using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

/// <summary>
/// Hybrid-search ensemble: dense vector search fused with BM25 and — when an
/// <see cref="ISparseEmbeddingGenerator"/> is registered and the store implements
/// <see cref="ISparseSearchable"/> — a learned sparse (SPLADE) arm, all merged by weighted
/// Reciprocal Rank Fusion. Degraded, never broken: a failed BM25 or sparse arm is logged and
/// the remaining arms are fused; dense-only results are returned when both fail.
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

        if (!opts.UseHybridSearch)
            return await next(ctx, ct).ConfigureAwait(false);

        var ensembleOpts = opts.EnsembleOptions ?? new EnsembleOptions();
        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
        };

        var queryVector = await QueryVectorResolver.ResolveAsync(opts, ctx.Query, Embedder, ct).ConfigureAwait(false);

        var denseTask = VectorStore.SearchAsync(queryVector, searchOptions, ct);

        // Sparse arm starts concurrently with the dense/BM25 arms when it can run: not
        // disabled per call (UseSparseSearch null follows UseHybridSearch, already true
        // here), a generator is registered, and the store is sparse-capable.
        Task<IReadOnlyList<SearchResult>?>? sparseTask = null;
        if (opts.UseSparseSearch != false && SparseGenerator is not null && VectorStore is ISparseSearchable sparseStore)
            sparseTask = SearchSparseSafeAsync(sparseStore, ctx, searchOptions, ct);

        IReadOnlyList<(TextChunk chunk, double score)>? bm25Hits;
        try
        {
            bm25Hits = Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
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
