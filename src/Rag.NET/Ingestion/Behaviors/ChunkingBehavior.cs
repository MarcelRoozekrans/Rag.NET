using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ChunkingBehavior : IIngestionBehavior
{
    public ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ctx.Chunks.Count == 0)
            return ValueTask.FromResult(new IngestionResult
            {
                DocumentId = ctx.Metadata.DocumentId,
                ChunksStored = 0,
            });

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Chunking,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.Chunks.Count,
            Total = ctx.Chunks.Count,
            Message = $"Chunked into {ctx.Chunks.Count} chunks",
        });

        return next(ctx, ct);
    }
}
