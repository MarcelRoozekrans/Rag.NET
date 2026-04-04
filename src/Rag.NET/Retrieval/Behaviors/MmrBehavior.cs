using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class MmrBehavior : IRetrievalBehavior
{
    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseMmr)
            return await next(ctx, ct).ConfigureAwait(false);

        var candidateCount = ctx.Options.MmrCandidateCount ?? ctx.Options.TopK * 3;
        if (candidateCount < ctx.Options.TopK)
            RagPipelineLog.MmrCandidateCountLessThanTopK(ctx.Logger, candidateCount, ctx.Options.TopK);

        var candidates = await next(ctx with { Options = ctx.Options with { TopK = candidateCount, UseMmr = false } }, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0) return candidates;

        try
        {
            var selected = await MmrSelector.SelectAsync(
                ctx.Query, candidates, Embedder,
                topK: ctx.Options.TopK,
                lambda: ctx.Options.MmrLambda,
                cancellationToken: ct).ConfigureAwait(false);

            return selected;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.MmrSelectionFailed(ctx.Logger, ctx.Query, ex);
            return candidates;
        }
    }
}
