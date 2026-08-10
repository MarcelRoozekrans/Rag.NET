using Rag.NET.Abstractions;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ChunkSanitiserBehavior : IIngestionBehavior
{
    // Note: [Inject] rather than [Inject(Required = false)] — ZeroAlloc.Inject does not
    // generate correct code for Required=false on IEnumerable<T> properties.
    // Microsoft DI always resolves IEnumerable<T> as an empty collection when no
    // implementations are registered, so this is effectively optional at runtime.
    [Inject] public IEnumerable<IChunkSanitiser> Sanitisers { get; set; } = [];

    public ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var sanitiserList = Sanitisers as IList<IChunkSanitiser> ?? [..Sanitisers];
        if (sanitiserList.Count == 0)
            return next(ctx, ct);

        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            var text = ctx.Chunks[i].Text;
            var metadata = (IReadOnlyDictionary<string, MetadataValue>)ctx.Chunks[i].Metadata;
            foreach (var sanitiser in sanitiserList)
                text = sanitiser.Sanitise(text, metadata);
            if (!ReferenceEquals(text, ctx.Chunks[i].Text))
                ctx.Chunks[i] = ctx.Chunks[i] with { Text = text };
        }

        return next(ctx, ct);
    }
}
