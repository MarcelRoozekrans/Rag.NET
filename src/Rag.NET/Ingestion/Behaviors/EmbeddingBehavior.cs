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
        var texts = ctx.Chunks.Select(c => c.Text).ToList();

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.embed");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);
        activity?.SetTag("chunk.count", texts.Count);

        var sw = Stopwatch.StartNew();
        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            embeddings = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            RagTelemetry.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds);
        }

        ctx.EmbeddedChunks.AddRange(
            ctx.Chunks.Zip(embeddings, (chunk, embedding) =>
                new EmbeddedChunk { Chunk = chunk, Embedding = embedding.Vector }));

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
}
