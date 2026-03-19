using System.Runtime.InteropServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParentDocumentIngestionBehavior : IIngestionBehavior
{
    [Inject(Required = false)] public IParentChunkStore? ParentStore { get; set; }
    [Inject(Required = false)] public ParentDocumentOptions? ParentOptions { get; set; }
    [Inject] public IEnumerable<IDocumentParser> Parsers { get; set; } = null!;
    [Inject] public IChunkingStrategy ChunkingStrategy { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ParentOptions is null || ParentStore is null)
            return await next(ctx, ct).ConfigureAwait(false);

        if (!ctx.Stream.CanSeek)
            throw new InvalidOperationException(
                "Parent-document retrieval requires a seekable stream. Wrap the stream in a MemoryStream before calling IngestAsync.");

        ctx.Stream.Position = 0;

        var parentChunkingOptions = new ChunkingOptions
        {
            MaxChunkSize = ParentOptions.ParentChunkSize,
            Overlap = ParentOptions.ParentOverlap,
        };

        var parser = Parsers.First(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"));
        var parentBoundaries = new List<(int start, int end)>();
        var parentIndex = 0;

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            await foreach (var parentChunk in ChunkingStrategy.ChunkAsync(section, parentChunkingOptions, ct).ConfigureAwait(false))
            {
                ParentStore.Add(ctx.Metadata.DocumentId, parentIndex, parentChunk.Text);
                parentBoundaries.Add((parentChunk.StartPosition, parentChunk.EndPosition));
                parentIndex++;
            }
        }

        foreach (ref readonly var child in CollectionsMarshal.AsSpan(ctx.Chunks))
        {
            var pIdx = ParentChunkKeyHelper.FindParentIndex(parentBoundaries, child.StartPosition);
            child.Metadata[ParentChunkKeyHelper.ParentKeyMetadata] =
                ParentChunkKeyHelper.GetParentKey(ctx.Metadata.DocumentId, pIdx);
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
