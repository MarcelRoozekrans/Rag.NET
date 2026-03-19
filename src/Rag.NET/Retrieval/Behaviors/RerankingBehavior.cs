using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class RerankingBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IReranker? Reranker { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseReranking || Reranker is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var candidateCount = ctx.Options.CandidateCount ?? ctx.Options.TopK * 3;
        var searchResults = await next(
            ctx with { Options = ctx.Options with { TopK = candidateCount, UseReranking = false } },
            ct).ConfigureAwait(false);

        try
        {
            var reranked = await Reranker.RerankAsync(ctx.Query, searchResults, ct).ConfigureAwait(false);
            var results = reranked
                .OrderByDescending(r => r.RelevanceScore)
                .Take(ctx.Options.TopK)
                .Select(r => r.SearchResult)
                .ToList()
                .AsReadOnly();
            RagPipelineLog.RerankingCompleted(ctx.Logger, searchResults.Count, results.Count);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RerankingFailed(ctx.Logger, ctx.Query, ex);
            return searchResults;
        }
    }
}
