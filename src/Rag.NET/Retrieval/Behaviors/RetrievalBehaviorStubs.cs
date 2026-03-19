// Stub implementations for retrieval behaviors — to be replaced in a future task.
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class ResultCacheBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class LostInTheMiddleBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class MmrBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class RedundancyFilterBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class ParentDocumentRetrievalBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class RerankingBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class MultiQueryBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class HydeBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class EmbeddingCacheBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class VectorStoreBehavior : IRetrievalBehavior
{
    public ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
        => next(ctx, ct);
}
