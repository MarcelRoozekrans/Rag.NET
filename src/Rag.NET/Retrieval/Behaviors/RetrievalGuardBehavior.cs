using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class RetrievalGuardBehavior : IRetrievalBehavior
{
    // Note: [Inject] rather than [Inject(Required = false)] — ZeroAlloc.Inject does not
    // generate correct code for Required=false on IEnumerable<T> properties.
    // Microsoft DI always resolves IEnumerable<T> as an empty collection when no
    // implementations are registered, so this is effectively optional at runtime.
    [Inject] public IEnumerable<IRetrievalGuard> Guards { get; set; } = [];

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        var guardList = Guards as IList<IRetrievalGuard> ?? [..Guards];
        if (guardList.Count == 0)
            return results;

        foreach (var guard in guardList)
            results = guard.Inspect(results);

        return results;
    }
}
