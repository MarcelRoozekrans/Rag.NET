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

            // A reranker that returns fewer results than were asked for has silently decided the
            // answer's size. Cohere's TopN defaulted to 5 and did exactly that: Take(TopK) became
            // a no-op and an answer meant to use 20 chunks used 5, with nothing logged and the
            // ONNX reranker behaving differently for the same configuration (issue #94).
            if (results.Count < ctx.Options.TopK && searchResults.Count >= ctx.Options.TopK)
                RagPipelineLog.RerankingReturnedFewerThanRequested(
                    ctx.Logger, Reranker.GetType().Name, results.Count, ctx.Options.TopK);

            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.RerankingFailed(ctx.Logger, ctx.Query, ex);

            // Truncate on the way out. This used to return the whole candidate list, so a failed
            // reranker handed back CandidateCount results — three times TopK by default — and a
            // failure silently *widened* the caller's request instead of degrading to it.
            return searchResults.Count <= ctx.Options.TopK
                ? searchResults
                : searchResults.Take(ctx.Options.TopK).ToList().AsReadOnly();
        }
    }
}
