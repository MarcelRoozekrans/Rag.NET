using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class StorageBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Required = false)] public IRagDataManager? DataManager { get; set; }
    [Inject(Required = false)] public ILogger<StorageBehavior>? Logger { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.store");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
        activity?.SetTag("chunk.count", ctx.EmbeddedChunks.Count);
        activity?.SetTag("vector_store", VectorStore.GetType().Name);

        await VectorStore.StoreAsync(ctx.EmbeddedChunks, ct).ConfigureAwait(false);
        await StoreSparseVectorsAsync(ctx, ct).ConfigureAwait(false);

        RagTelemetry.ChunksStored.Add(ctx.EmbeddedChunks.Count);

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

    /// <summary>
    /// Persists the sparse vectors computed by <see cref="SparseEmbeddingBehavior"/>, when
    /// present and the store is sparse-capable. Degraded, never broken: a sparse storage
    /// failure is logged and ingestion completes dense-only (the dense vectors are already
    /// stored at this point).
    /// </summary>
    private async Task StoreSparseVectorsAsync(IngestionContext ctx, CancellationToken ct)
    {
        if (ctx.SparseVectors is not { Count: > 0 } sparseVectors || VectorStore is not ISparseSearchable sparseStore)
            return;

        if (sparseVectors.Count != ctx.EmbeddedChunks.Count)
        {
            RagPipelineLog.SparseStorageFailed(
                (ILogger?)Logger ?? NullLogger.Instance, ctx.Metadata.DocumentId.Value,
                new InvalidOperationException(
                    $"Sparse vector count ({sparseVectors.Count}) does not match embedded chunk count ({ctx.EmbeddedChunks.Count})."));
            return;
        }

        try
        {
            var items = new List<(EmbeddedChunk Chunk, SparseVector Sparse)>(sparseVectors.Count);
            for (var i = 0; i < sparseVectors.Count; i++)
                items.Add((ctx.EmbeddedChunks[i], sparseVectors[i]));

            await sparseStore.StoreSparseAsync(items, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.SparseStorageFailed(
                (ILogger?)Logger ?? NullLogger.Instance, ctx.Metadata.DocumentId.Value, ex);
        }
    }
}
