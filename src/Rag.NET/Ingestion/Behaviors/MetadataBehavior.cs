using System.Runtime.InteropServices;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class MetadataBehavior : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        foreach (ref var chunk in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            foreach (var tag in ctx.Metadata.Tags)
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            chunk.Metadata.TryAdd("document_id", ctx.Metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", ctx.Metadata.FileName);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
