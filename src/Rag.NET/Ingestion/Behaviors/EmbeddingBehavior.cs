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
        var pending = new List<(int Index, string Text)>();
        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            if (ctx.Chunks[i].Embedding is null)
            {
                pending.Add((i, ctx.Chunks[i].Text));
            }
        }

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.embed");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
        activity?.SetTag("chunk.count", ctx.Chunks.Count);
        activity?.SetTag("chunk.precomputed", ctx.Chunks.Count - pending.Count);

        GeneratedEmbeddings<Embedding<float>>? generated = null;
        if (pending.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var texts = pending.Select(p => p.Text).ToList();
                generated = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                RagTelemetry.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds);
            }
        }

        AssembleEmbeddedChunks(ctx, pending, generated);

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Embedding,
            DocumentId = ctx.Metadata.DocumentId,
            Current = ctx.EmbeddedChunks.Count,
            Total = ctx.EmbeddedChunks.Count,
            Message = $"Generated {ctx.EmbeddedChunks.Count} embeddings",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private static void AssembleEmbeddedChunks(
        IngestionContext ctx,
        List<(int Index, string Text)> pending,
        GeneratedEmbeddings<Embedding<float>>? generated)
    {
        var byIndex = new ReadOnlyMemory<float>[ctx.Chunks.Count];
        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            if (ctx.Chunks[i].Embedding is { } pre)
            {
                byIndex[i] = pre;
            }
        }

        for (var p = 0; p < pending.Count; p++)
        {
            byIndex[pending[p].Index] = generated![p].Vector;
        }

        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = ctx.Chunks[i], Embedding = byIndex[i] });
        }
    }
}
