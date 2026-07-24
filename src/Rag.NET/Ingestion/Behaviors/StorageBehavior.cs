using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class StorageBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject] public IBm25Index Bm25Index { get; set; } = null!;
    [Inject(Required = false)] public IRagDataManager? DataManager { get; set; }
    [Inject(Required = false)] public IEmbeddingVersionStore? VersionStore { get; set; }
    [Inject(Required = false)] public IEmbeddingGenerator<string, Embedding<float>>? Embedder { get; set; }
    [Inject(Required = false)] public EmbeddingVersioningOptions? VersioningOptions { get; set; }
    [Inject(Required = false)] public ILogger<StorageBehavior>? Logger { get; set; }

    /// <summary>One-time flag for the identity-unresolvable warning (0 = not yet logged).</summary>
    private int _identityWarningLogged;

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

        await StampEmbeddingVersionAsync(ctx, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Stamps the embedding model version after a successful store, when an
    /// <see cref="IEmbeddingVersionStore"/> is registered and the model identity resolves.
    /// Degraded, never broken: a stamp failure is logged and ingestion still succeeds
    /// (the vectors are already stored). An unresolvable identity disables stamping with
    /// a one-time warning. Documents with zero chunks are not stamped (no dimension).
    /// </summary>
    private async Task StampEmbeddingVersionAsync(IngestionContext ctx, CancellationToken ct)
    {
        if (VersionStore is null || ctx.EmbeddedChunks.Count == 0)
            return;

        var modelId = EmbeddingModelIdentity.Resolve(Embedder, VersioningOptions);
        if (modelId is null)
        {
            if (Interlocked.Exchange(ref _identityWarningLogged, 1) == 0)
                RagPipelineLog.EmbeddingVersionIdentityUnresolvable((ILogger?)Logger ?? NullLogger.Instance);
            return;
        }

        try
        {
            var dimension = ctx.EmbeddedChunks[0].Embedding.Length;
            await VersionStore.SetAsync(ctx.Metadata.DocumentId.Value, modelId, dimension, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.EmbeddingVersionStampFailed(
                (ILogger?)Logger ?? NullLogger.Instance, ctx.Metadata.DocumentId.Value, ex);
        }
    }
}
