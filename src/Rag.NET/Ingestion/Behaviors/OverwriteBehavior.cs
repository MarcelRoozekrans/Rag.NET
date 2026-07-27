using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

/// <summary>
/// Purges a document from every store <em>before</em> the pipeline does any work, when the
/// caller passed <see cref="Models.Options.IngestionOptions.Overwrite"/>.
/// <para>
/// This is not what makes re-ingest a replace — <see cref="StorageBehavior"/> is, and it removes
/// the append-only entries unconditionally right before re-adding them. What remains here is the
/// stronger, opt-in guarantee: the document is gone up front regardless of what happens next, so
/// an overwrite whose new content fails to parse or yields no chunks still leaves nothing behind.
/// The two BM25/data-manager removals therefore overlap on the happy path (removal is idempotent)
/// and differ only on those paths, which is the behaviour <c>Overwrite</c> already had.
/// </para>
/// <para>
/// <see cref="IVectorStore.DeleteByDocumentIdAsync"/> is gated here and <em>only</em> here:
/// making it unconditional would change what <c>Overwrite</c> means for every existing caller.
/// See <c>docs/plans/2026-07-27-service-bus-ingestion-design.md</c> §1.
/// </para>
/// </summary>
[Singleton]
public sealed class OverwriteBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Required = false)] public IRagDataManager? DataManager { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.Options?.Overwrite == true)
        {
            await VectorStore.DeleteByDocumentIdAsync(ctx.Metadata.DocumentId, ct).ConfigureAwait(false);
            Bm25Index.Remove(ctx.Metadata.DocumentId);
            DataManager?.Remove(ctx.Metadata.DocumentId);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
