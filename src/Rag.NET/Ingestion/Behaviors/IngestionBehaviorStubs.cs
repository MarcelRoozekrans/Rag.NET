// Stub implementations for ingestion behaviors — to be replaced in a future task.
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParentDocumentIngestionBehavior : IIngestionBehavior
{
    public ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class EmbeddingBehavior : IIngestionBehavior
{
    public ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
        => next(ctx, ct);
}

[Singleton]
public sealed class StorageBehavior : IIngestionBehavior
{
    public ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
        => next(ctx, ct);
}
