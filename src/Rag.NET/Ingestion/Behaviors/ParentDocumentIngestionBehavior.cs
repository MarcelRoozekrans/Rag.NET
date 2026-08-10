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

        RequireUsableParentChunking(parentChunkingOptions, ParentOptions);

        // FirstOrDefault plus an explicit throw, matching ParseBehavior: First() surfaces the
        // identical "nothing parses this" condition as a bare InvalidOperationException, which
        // PipelineIngestor does not map to RagError.NoParserFound, so only one of the two paths
        // was catchable as the documented error (issue #130).
        var parentContentType = DocumentContentTypeResolver.Resolve(ctx.Metadata);
        var parser = Parsers.FirstOrDefault(p => p.CanParse(parentContentType))
            ?? throw new NoParserFoundException(parentContentType);
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

    /// <summary>
    /// Rejects parent chunking this behaviour cannot make progress on.
    /// <para>
    /// These options never met <c>ChunkingOptionsValidator</c>: the ingestion-time check runs
    /// over the <i>main</i> chunking options, not this synthesised pair, so the parent path had
    /// no validation at all (issue #93). <c>UseParentDocumentRetrieval</c> now rejects it at
    /// registration; this is the second line, because <see cref="ParentDocumentOptions"/> is a
    /// mutable singleton and can reach the container without going through the builder.
    /// </para>
    /// <para>
    /// Both checks run, deliberately. <see cref="ChunkingOptions.Validate"/> alone catches a
    /// non-positive <c>ParentChunkSize</c> — any overlap ≥ 0 is then ≥ the chunk size — but not a
    /// negative <c>ParentOverlap</c>, which is the quieter half of #93: it leaves gaps between
    /// parent spans, and a child chunk landing in one falls through to the "last parent"
    /// fallback, so retrieval answers fluently from the wrong part of the document.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The sizing cannot be chunked.</exception>
    private static void RequireUsableParentChunking(
        ChunkingOptions parentChunkingOptions, ParentDocumentOptions parentOptions)
    {
        if (!new ChunkingOptionsValidator().Validate(parentChunkingOptions).IsValid)
        {
            throw new InvalidOperationException(
                "ParentDocumentOptions produced invalid chunking options: " +
                $"ParentChunkSize={parentOptions.ParentChunkSize}, " +
                $"ParentOverlap={parentOptions.ParentOverlap}. Both must be at least 0, and " +
                "ParentChunkSize must be greater than 0.");
        }
    }
}
