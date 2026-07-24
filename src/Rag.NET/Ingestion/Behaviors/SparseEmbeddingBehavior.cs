using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

/// <summary>
/// Computes sparse (SPLADE) vectors for the embedded chunks and stashes them on
/// <see cref="IngestionContext.SparseVectors"/> for <see cref="StorageBehavior"/> to persist.
/// Transparent when no <see cref="ISparseEmbeddingGenerator"/> is registered or the vector
/// store is not <see cref="ISparseSearchable"/>. Degraded, never broken: generation failure
/// is logged and ingestion proceeds dense-only.
/// </summary>
[Singleton]
public sealed class SparseEmbeddingBehavior : IIngestionBehavior
{
    [Inject] public IVectorStore VectorStore { get; set; } = null!;
    [Inject(Required = false)] public ISparseEmbeddingGenerator? SparseGenerator { get; set; }
    [Inject(Required = false)] public ILogger<SparseEmbeddingBehavior>? Logger { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (SparseGenerator is null || VectorStore is not ISparseSearchable || ctx.EmbeddedChunks.Count == 0)
            return await next(ctx, ct).ConfigureAwait(false);

        try
        {
            var vectors = new List<SparseVector>(ctx.EmbeddedChunks.Count);
            for (var i = 0; i < ctx.EmbeddedChunks.Count; i++)
            {
                var sparse = await SparseGenerator.GenerateAsync(ctx.EmbeddedChunks[i].Chunk.Text, ct)
                    .ConfigureAwait(false);
                vectors.Add(sparse);
            }

            ctx.SparseVectors = vectors;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.SparseVectors = null;
            RagPipelineLog.SparseEmbeddingFailed(
                (ILogger?)Logger ?? NullLogger.Instance, ctx.Metadata.DocumentId.Value, ex);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
