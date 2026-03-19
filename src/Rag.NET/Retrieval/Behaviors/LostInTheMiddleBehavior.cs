using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class LostInTheMiddleBehavior : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        return ctx.Options.UseLostInTheMiddleReordering ? LostInTheMiddleReorderer.Reorder(results) : results;
    }
}
