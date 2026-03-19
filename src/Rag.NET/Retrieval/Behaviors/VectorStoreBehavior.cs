using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class VectorStoreBehavior : IRetrievalBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var opts = ctx.Options;
        var searchOptions = new SearchOptions
        {
            TopK = opts.TopK,
            MinScore = opts.MinScore,
            MetadataFilter = opts.MetadataFilter,
            UseHybridSearch = opts.UseHybridSearch,
        };

        var textToEmbed = opts.EmbeddingTextOverride ?? ctx.Query;
        var queryEmbeddings = await Embedder.GenerateAsync([textToEmbed], cancellationToken: ct).ConfigureAwait(false);

        IReadOnlyList<SearchResult> results;
        string searchMode;

        if (opts.UseHybridSearch)
        {
            if (VectorStore is IHybridSearchable hybrid)
            {
                searchMode = "hybrid-native";
                results = await hybrid.HybridSearchAsync(ctx.Query, queryEmbeddings[0].Vector, searchOptions, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                searchMode = "hybrid-bm25-fallback";
                var denseTask = VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct);
                var bm25Hits = Bm25Index.Search(ctx.Query, topK: searchOptions.TopK);
                var dense = await denseTask.ConfigureAwait(false);
                results = RrfMerger.Merge(dense, bm25Hits, searchOptions.TopK);
            }
        }
        else
        {
            searchMode = "dense";
            results = await VectorStore.SearchAsync(queryEmbeddings[0].Vector, searchOptions, ct).ConfigureAwait(false);
        }

        RagPipelineLog.VectorStoreSearchCompleted(ctx.Logger, searchMode, results.Count);
        // Terminal — does not call next
        return results;
    }
}
