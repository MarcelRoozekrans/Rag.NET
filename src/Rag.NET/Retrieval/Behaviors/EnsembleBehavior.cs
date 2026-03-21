using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class EnsembleBehavior : IRetrievalBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;

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

        var textToEmbed = opts.EmbeddingTextOverride ?? ctx.Query;
        var queryEmbeddings = await Embedder.GenerateAsync([textToEmbed], cancellationToken: ct).ConfigureAwait(false);

        var denseTask = VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct);

        IReadOnlyList<(TextChunk chunk, double score)> bm25Hits;
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
            var dense = await denseTask.ConfigureAwait(false);
            return dense;
        }

        var denseResults = await denseTask.ConfigureAwait(false);
        var merged = RrfMerger.Merge(denseResults, bm25Hits, opts.TopK, ensembleOpts);

        RagPipelineLog.VectorStoreSearchCompleted(ctx.Logger, "ensemble-rrf", merged.Count);
        return merged;
    }
}
