using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

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
