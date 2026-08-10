using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class VectorStoreBehavior : IRetrievalBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

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
        };

        var queryVector = await QueryVectorResolver.ResolveAsync(opts, ctx.Query, Embedder, ct).ConfigureAwait(false);

        var results = await VectorStore.SearchAsync(queryVector, searchOptions, ct).ConfigureAwait(false);
        // Terminal — does not call next
        return results;
    }
}
