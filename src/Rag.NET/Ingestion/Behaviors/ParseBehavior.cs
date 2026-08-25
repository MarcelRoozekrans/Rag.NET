using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParseBehavior : IIngestionBehavior
{
    [Inject] public IEnumerable<IDocumentParser> Parsers { get; set; } = null!;
    [Inject] public IChunkingStrategy ChunkingStrategy { get; set; } = null!;
    [Inject] public ChunkingOptions ChunkingOptions { get; set; } = null!;

    /// <summary>
    /// Optional post-processing strategy applied after chunking.
    /// Set by the DI composition root (e.g. RagBuilder) when a refinement strategy is registered.
    /// Not decorated with [Inject] because ZeroAlloc.Inject does not support nullable optional properties.
    /// </summary>
    public IChunkRefinementStrategy? RefinementStrategy { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.parse");
        activity?.SetTag("document.id", ctx.Metadata.DocumentId.Value);

        var contentType = ctx.Metadata.ResolveContentType();
        var parser = Parsers.FirstOrDefault(p => p.CanParse(contentType));
        if (parser is null)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, $"No parser for {contentType}");
            throw new NoParserFoundException(contentType);
        }

        activity?.SetTag("parser.type", parser.GetType().Name);

        if (ChunkingStrategy is IDocumentChunkingStrategy docStrategy)
            await ChunkDocumentAsync(ctx, parser, docStrategy, ct).ConfigureAwait(false);
        else
            await ChunkPerSectionAsync(ctx, parser, ct).ConfigureAwait(false);

        activity?.SetTag("section.count", ctx.Sections.Count);
        activity?.SetTag("chunk.count", ctx.Chunks.Count);

        if (RefinementStrategy is not null)
            await ApplyRefinementAsync(ctx, ct).ConfigureAwait(false);

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Parsing,
            DocumentId = ctx.Metadata.DocumentId,
            Message = "Parsing complete",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private async Task ApplyRefinementAsync(IngestionContext ctx, CancellationToken ct)
    {
        var raw = ctx.Chunks.ToAsyncEnumerable();
        var refined = new List<TextChunk>();
        await foreach (var chunk in RefinementStrategy!.RefineAsync(raw, ct).ConfigureAwait(false))
            refined.Add(chunk);
        ctx.Chunks.Clear();
        ctx.Chunks.AddRange(refined);
    }

    private async Task ChunkDocumentAsync(
        IngestionContext ctx, IDocumentParser parser, IDocumentChunkingStrategy docStrategy, CancellationToken ct)
    {
        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
            ctx.Sections.Add(section);

        await foreach (var chunk in docStrategy.ChunkDocumentAsync(
            ctx.Sections.ToAsyncEnumerable(), ChunkingOptions, ct).ConfigureAwait(false))
            ctx.Chunks.Add(chunk);
    }

    private async Task ChunkPerSectionAsync(IngestionContext ctx, IDocumentParser parser, CancellationToken ct)
    {
        var headingBreadcrumbs = new string?[6];
        var documentChunkIndex = 0;

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            // Computed BEFORE the skip below, deliberately: it is what records this section's
            // heading in the breadcrumb array. Skipping earlier would leave this level unset,
            // and a child of "Section 2" would be labelled "Chapter 1 > Subsection".
            var headingMetadata = BuildHeadingMetadata(section, headingBreadcrumbs);

            if (IsNothingButItsHeading(section))
            {
                ctx.Sections.Add(section);
                continue;
            }

            await foreach (var chunk in ChunkingStrategy.ChunkAsync(section, ChunkingOptions, ct).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);

                // Strategies number from zero per ChunkAsync call — i.e. per section — but
                // ChunkIndex must be unique across the document: DeterministicChunkId, every
                // dedup path and parent-chunk lookup key on (DocumentId, ChunkIndex). Copy only
                // when the index actually moves, so the common single-section document keeps its
                // chunk instances rather than allocating a copy per chunk to change nothing.
                ctx.Chunks.Add(chunk.ChunkIndex == documentChunkIndex
                    ? chunk
                    : chunk with { ChunkIndex = documentChunkIndex });
                documentChunkIndex++;
            }

            ctx.Sections.Add(section);
        }
    }

    /// <summary>
    /// Whether a section carries no text beyond its own heading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parsers prepend the heading to the section body — <c>HtmlDocumentParser.BuildHeadingSection</c>
    /// does it explicitly — so a heading with nothing under it before the next heading yields a
    /// section whose text <i>is</i> the heading. The parser's own empty-section guard cannot catch
    /// that, because a heading is not whitespace.
    /// </para>
    /// <para>
    /// Chunking it produced entries like <c>"text": "Section 2"</c> in the index (#366): they score
    /// on heading-shaped queries, occupy a retrieval slot, and give an answer nothing to work with —
    /// while <c>heading</c>, <c>heading_level</c> and <c>heading_breadcrumb</c> already travel as
    /// metadata on every chunk that does have content.
    /// </para>
    /// </remarks>
    /// <param name="section">The section about to be chunked.</param>
    /// <returns>Whether the section would only ever produce a copy of its own heading.</returns>
    private static bool IsNothingButItsHeading(DocumentSection section) =>
        section.Heading is { } heading
        && string.Equals(section.Text.Trim(), heading.Trim(), StringComparison.Ordinal);

    private static Dictionary<string, string>? BuildHeadingMetadata(DocumentSection section, string?[] breadcrumbs)
    {
        if (section.HeadingLevel is not { } level || level < 1 || level > 6 || section.Heading is null)
            return null;

        breadcrumbs[level - 1] = section.Heading;
        foreach (ref var slot in breadcrumbs.AsSpan(level))
            slot = null;

        var parts = new List<string>(level);
        foreach (var h in breadcrumbs[..level])
            if (h is not null) parts.Add(h);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["heading"] = section.Heading,
            ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["heading_breadcrumb"] = string.Join(" > ", parts),
        };
    }
}
