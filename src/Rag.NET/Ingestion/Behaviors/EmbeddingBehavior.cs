using System.Diagnostics;
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class EmbeddingBehavior : IIngestionBehavior
{
    /// <summary>Fallback when the caller supplied no <see cref="IngestionContext.Options"/>.</summary>
    private static readonly IngestionOptions DefaultOptions = new();

    [Inject] public IEmbeddingGenerator<string, Embedding<float>> Embedder { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var pendingIndices = new List<int>();
        var texts = new List<string>();
        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            // Empty embeddings are treated as absent (see TextChunk.Embedding remarks).
            if (ctx.Chunks[i].Embedding is not { IsEmpty: false })
            {
                pendingIndices.Add(i);
                texts.Add(ctx.Chunks[i].Text);
            }
        }

        var precomputedCount = ctx.Chunks.Count - pendingIndices.Count;
        var options = ctx.Options ?? DefaultOptions;
        // The <= comparison (rather than pure ceiling division) also guards against
        // int overflow when EmbedBatchSize is set near int.MaxValue.
        var batchCount = texts.Count == 0
            ? 0
            : texts.Count <= options.EmbedBatchSize
                ? 1
                : (texts.Count + options.EmbedBatchSize - 1) / options.EmbedBatchSize;

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.embed");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
        activity?.SetTag("chunk.count", ctx.Chunks.Count);
        activity?.SetTag("chunk.precomputed", precomputedCount);
        activity?.SetTag("embed.batches", batchCount);

        ReadOnlyMemory<float>[]? pendingVectors = null;
        if (texts.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                pendingVectors = batchCount == 1
                    ? await EmbedSingleBatchAsync(ctx, texts, ct).ConfigureAwait(false)
                    : await EmbedBatchedAsync(ctx, texts, options, batchCount, ct).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                RagTelemetry.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds);
            }
        }

        AssembleEmbeddedChunks(ctx, pendingIndices, pendingVectors);

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Embedding,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.EmbeddedChunks.Count,
            Total = ctx.EmbeddedChunks.Count,
            Message = $"Embedded {pendingIndices.Count} chunks ({precomputedCount} precomputed)",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The pre-batching fast path: all pending texts embed in one generator call.
    /// Documents at or below <see cref="IngestionOptions.EmbedBatchSize"/> pending
    /// chunks always take this path, preserving the original single-call semantics.
    /// </summary>
    private async Task<ReadOnlyMemory<float>[]> EmbedSingleBatchAsync(
        IngestionContext ctx, List<string> texts, CancellationToken ct)
    {
        var generated = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        if (generated.Count != texts.Count)
        {
            throw new InvalidOperationException(
                $"Embedding generator returned {generated.Count} embeddings for {texts.Count} inputs (document '{ctx.Metadata.DocumentId.Value}').");
        }

        var vectors = new ReadOnlyMemory<float>[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            vectors[i] = generated[i].Vector;
        }

        return vectors;
    }

    /// <summary>
    /// Slices the pending texts into batches of <see cref="IngestionOptions.EmbedBatchSize"/>
    /// and embeds them concurrently, bounded by
    /// <see cref="IngestionOptions.MaxConcurrentEmbeddingBatches"/>.
    /// </summary>
    private async Task<ReadOnlyMemory<float>[]> EmbedBatchedAsync(
        IngestionContext ctx, List<string> texts, IngestionOptions options, int batchCount, CancellationToken ct)
    {
        var vectors = new ReadOnlyMemory<float>[texts.Count];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxConcurrentEmbeddingBatches,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, batchCount), parallelOptions, async (batch, token) =>
        {
            var start = batch * options.EmbedBatchSize;
            var batchTexts = texts.GetRange(start, Math.Min(options.EmbedBatchSize, texts.Count - start));
            var generated = await Embedder.GenerateAsync(batchTexts, cancellationToken: token).ConfigureAwait(false);
            if (generated.Count != batchTexts.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding generator returned {generated.Count} embeddings for {batchTexts.Count} inputs in batch {batch + 1}/{batchCount} (document '{ctx.Metadata.DocumentId.Value}').");
            }

            // Thread safety: each batch writes only its own disjoint index range
            // [start, start + batchTexts.Count) of the shared array — no two batches
            // ever touch the same slot, so no locking is needed.
            for (var i = 0; i < generated.Count; i++)
            {
                vectors[start + i] = generated[i].Vector;
            }
        }).ConfigureAwait(false);

        return vectors;
    }

    private static void AssembleEmbeddedChunks(
        IngestionContext ctx,
        List<int> pendingIndices,
        ReadOnlyMemory<float>[]? pendingVectors)
    {
        var byIndex = new ReadOnlyMemory<float>[ctx.Chunks.Count];
        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            if (ctx.Chunks[i].Embedding is { IsEmpty: false } pre)
            {
                byIndex[i] = pre;
            }
        }

        for (var p = 0; p < pendingIndices.Count; p++)
        {
            byIndex[pendingIndices[p]] = pendingVectors![p];
        }

        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = ctx.Chunks[i], Embedding = byIndex[i] });
        }
    }
}
