using Microsoft.Extensions.AI;
using Rag.NET.Models;
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
        var embeddings = await Embedder.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);

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
