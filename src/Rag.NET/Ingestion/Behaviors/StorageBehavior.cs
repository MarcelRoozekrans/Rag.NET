using System.Runtime.InteropServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class StorageBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Required = false)] public IRagDataManager? DataManager { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        await VectorStore.StoreAsync(ctx.EmbeddedChunks, ct).ConfigureAwait(false);

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Storing,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.EmbeddedChunks.Count,
            Total = ctx.EmbeddedChunks.Count,
            Message = $"Stored {ctx.EmbeddedChunks.Count} chunks",
        });

        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(ctx.EmbeddedChunks))
            Bm25Index.Add(ctx.GetNextBm25DocId(), ec.Chunk);

        DataManager?.Add(ctx.Metadata, ctx.Chunks);

        // Terminal — does not call next
        return new IngestionResult
        {
            DocumentId = ctx.Metadata.DocumentId,
            ChunksStored = ctx.EmbeddedChunks.Count,
        };
    }
}
