using System.Diagnostics;
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class EmbeddingBehavior : IIngestionBehavior
{
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

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.embed");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
        activity?.SetTag("chunk.count", ctx.Chunks.Count);
        activity?.SetTag("chunk.precomputed", precomputedCount);

        GeneratedEmbeddings<Embedding<float>>? generated = null;
        if (pendingIndices.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                generated = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                RagTelemetry.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds);
            }
        }

        if (generated is not null && generated.Count != pendingIndices.Count)
        {
            throw new InvalidOperationException(
                $"Embedding generator returned {generated.Count} embeddings for {pendingIndices.Count} inputs (document '{ctx.Metadata.DocumentId.Value}').");
        }

        AssembleEmbeddedChunks(ctx, pendingIndices, generated);

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

    private static void AssembleEmbeddedChunks(
        IngestionContext ctx,
        List<int> pendingIndices,
        GeneratedEmbeddings<Embedding<float>>? generated)
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
            byIndex[pendingIndices[p]] = generated![p].Vector;
        }

        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = ctx.Chunks[i], Embedding = byIndex[i] });
        }
    }
}
