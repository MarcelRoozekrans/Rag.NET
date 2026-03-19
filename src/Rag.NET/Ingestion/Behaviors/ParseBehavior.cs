using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class ParseBehavior : IIngestionBehavior
{
    [Inject] public IEnumerable<IDocumentParser> Parsers { get; set; } = null!;
    [Inject] public IChunkingStrategy ChunkingStrategy { get; set; } = null!;
    [Inject] public ChunkingOptions ChunkingOptions { get; set; } = null!;

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        var parser = Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{ctx.Metadata.ContentType}'.");

        var headingBreadcrumbs = new string?[6];

        await foreach (var section in parser.ParseAsync(ctx.Stream, ctx.Metadata, ct).ConfigureAwait(false))
        {
            Dictionary<string, string>? headingMetadata = null;

            if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
            {
                headingBreadcrumbs[level - 1] = section.Heading;
                for (var idx = level; idx < 6; idx++) headingBreadcrumbs[idx] = null;

                var parts = new List<string>(level);
                foreach (var h in headingBreadcrumbs[..level])
                    if (h is not null) parts.Add(h);

                headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = section.Heading,
                    ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["heading_breadcrumb"] = string.Join(" > ", parts),
                };
            }

            await foreach (var chunk in ChunkingStrategy.ChunkAsync(section, ChunkingOptions, ct).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);

                ctx.Chunks.Add(chunk);
            }

            ctx.Sections.Add(section);
        }

        ctx.Progress?.Report(new()
        {
            Stage = IngestionProgressStage.Parsing,
            DocumentId = ctx.Metadata.DocumentId,
            Message = "Parsing complete",
        });

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
