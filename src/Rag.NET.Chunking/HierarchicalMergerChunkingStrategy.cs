using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

/// <summary>
/// Merges document sections into heading-subtree chunks using a streaming heading-stack algorithm.
/// Each chunk covers one heading and all body text under it up to <see cref="HierarchicalMergerOptions.MaxDepth"/>.
/// Implements <see cref="IDocumentChunkingStrategy"/> for pipeline use and
/// <see cref="IChunkingStrategy"/> as a per-section fallback.
/// </summary>
/// <remarks>
/// <b><see cref="ChunkingOptions"/> is deliberately ignored — chunks are unbounded above.</b>
/// A chunk here is one heading subtree, a semantic unit whose size the document decides, and
/// truncating it at <see cref="ChunkingOptions.MaxChunkSize"/> would defeat the strategy's
/// purpose; <see cref="ChunkingOptions.Overlap"/> has no meaning between disjoint subtrees. The
/// same holds for every template that delegates here — <c>BookChunkingStrategy</c>,
/// <c>LegalChunkingStrategy</c> and <c>AcademicPaperChunkingStrategy</c> — so setting either
/// option alongside any of them changes nothing. To bound chunk size on top of the heading
/// structure, register <c>UseSemanticRefinement()</c>, which sub-splits oversized chunks after
/// this strategy has shaped them. Recorded as the deliberate contract by Phase 4.1, closing the
/// Phase 3.16 finding that the option was silently ignored.
/// </remarks>
public sealed class HierarchicalMergerChunkingStrategy(HierarchicalMergerOptions options)
    : IDocumentChunkingStrategy, IChunkingStrategy
{
    private readonly Regex[][]? _compiledPatterns = options.HeadingPatterns is null ? null :
        options.HeadingPatterns
            .Select(level => level
                .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.Multiline, TimeSpan.FromSeconds(1)))
                .ToArray())
            .ToArray();

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions _,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new StringBuilder();
        var currentHeading = string.Empty;
        var currentLevel = int.MaxValue;
        var chunkIndex = 0;
        DocumentId? documentId = null;
        // Page range of the sections merged into the current chunk: min/max of the page numbers
        // that are present. Sections without a page (mixed sources) simply don't contribute —
        // a chunk touching page 3 plus an unpaginated section is still findable on page 3.
        var pages = PageRange.Empty;

        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            documentId ??= section.DocumentId;
            var level = DetectLevel(section);

            if (level is not null && level <= options.MaxDepth)
            {
                // Flush accumulated buffer as a chunk before starting the new heading
                if (buffer.Length > 0 || currentHeading.Length > 0)
                    yield return BuildChunk(documentId, chunkIndex++, currentHeading, currentLevel, buffer, pages);

                currentHeading = section.Heading ?? StripMarkdownPrefix(section.Text);
                currentLevel = level.Value;
                buffer.Clear();
                pages = PageRange.Empty.Fold(section.PageNumber);
            }
            else
            {
                // Body text or heading deeper than MaxDepth — fold into current chunk
                if (buffer.Length > 0)
                    buffer.AppendLine();
                buffer.Append(section.Text.Trim());
                pages = pages.Fold(section.PageNumber);
            }
        }

        // Flush the final accumulated chunk
        if (buffer.Length > 0 || currentHeading.Length > 0)
            yield return BuildChunk(documentId ?? new DocumentId("unknown"), chunkIndex, currentHeading, currentLevel, buffer, pages);
    }

    /// <inheritdoc/>
    /// <remarks>Fallback implementation: emits each section as a single chunk without merging.</remarks>
    public IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions _,
        CancellationToken cancellationToken = default)
    {
        TextChunk[] result =
        [
            new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = section.SectionIndex,
                Metadata = PageMetadata.ForPage(section.PageNumber),
            }
        ];
        return result.ToAsyncEnumerable();
    }

    private int? DetectLevel(DocumentSection section)
    {
        // Prefer parser-supplied heading level
        if (section.HeadingLevel.HasValue)
            return section.HeadingLevel;

        // Fall back to user-supplied regex patterns
        if (_compiledPatterns is null)
            return null;

        for (var i = 0; i < _compiledPatterns.Length; i++)
            foreach (var regex in _compiledPatterns[i])
                if (regex.IsMatch(section.Text))
                    return i + 1;

        return null;
    }

    private static TextChunk BuildChunk(
        DocumentId docId, int index, string heading, int level, StringBuilder body, in PageRange pages)
    {
        var bodyText = body.ToString().Trim();
        var text = heading.Length > 0
            ? $"{heading}\n\n{bodyText}"
            : bodyText;

        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (heading.Length > 0)
        {
            metadata["heading"] = heading;
            if (level < int.MaxValue)
                metadata["heading_level"] = level.ToString(CultureInfo.InvariantCulture);
        }

        pages.WriteTo(metadata);

        return new TextChunk
        {
            Text = text,
            DocumentId = docId,
            ChunkIndex = index,
            Metadata = metadata,
        };
    }

    private static string StripMarkdownPrefix(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return text.Trim();
    }
}
