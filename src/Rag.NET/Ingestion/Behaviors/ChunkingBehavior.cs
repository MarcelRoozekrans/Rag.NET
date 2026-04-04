using Rag.NET.Models;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ChunkingBehavior : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.chunk");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
        activity?.SetTag("chunk.count", ctx.Chunks.Count);

        if (ctx.Chunks.Count == 0)
            return new IngestionResult
            {
                DocumentId = ctx.Metadata.DocumentId,
                ChunksStored = 0,
            };

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Chunking,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.Chunks.Count,
            Total = ctx.Chunks.Count,
            Message = $"Chunked into {ctx.Chunks.Count} chunks",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
