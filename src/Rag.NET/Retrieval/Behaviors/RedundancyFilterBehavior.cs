using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class RedundancyFilterBehavior : IRetrievalBehavior
{
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        if (!ctx.Options.UseRedundancyFilter) return results;

        try
        {
            var filtered = await RedundancyFilter.FilterAsync(results, Embedder, ctx.Options.RedundancyThreshold, ct)
                .ConfigureAwait(false);
            RagPipelineLog.RedundancyFilterCompleted(ctx.Logger, results.Count, filtered.Count);
            return filtered;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RedundancyFilteringFailed(ctx.Logger, ctx.Query, ex);
            return results;
        }
    }
}
