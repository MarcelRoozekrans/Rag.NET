using Rag.NET.Ingestion;
using Rag.NET.Models;

namespace Rag.NET.GraphRag;

/// <summary>
/// Ingestion behavior that extracts a mind-map from the full document text.
/// Only runs when MindMapOptions.ExtractAtIngestion is true.
/// </summary>
public sealed class MindMapExtractionBehavior(MindMapExtractor extractor, MindMapOptions options)
    : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (options.ExtractAtIngestion && ctx.Chunks.Count > 0)
        {
            var fullText = string.Join("\n\n", ctx.Chunks.Select(c => c.Text));
            var documentId = ctx.Metadata.DocumentId.ToString();
            await extractor.ExtractAsync(fullText, documentId, ct).ConfigureAwait(false);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
