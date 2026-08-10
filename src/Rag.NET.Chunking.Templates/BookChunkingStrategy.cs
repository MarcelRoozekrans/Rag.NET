using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Book-shaped chunking: front-matter filtering (table of contents, index, foreword) over
/// <see cref="HierarchicalMergerChunkingStrategy"/>, with chapter metadata on every chunk.
/// </summary>
/// <remarks>
/// Delegates to <see cref="HierarchicalMergerChunkingStrategy"/>, which deliberately ignores
/// <see cref="ChunkingOptions"/> — a chunk is one heading subtree, unbounded above, and
/// <see cref="ChunkingOptions.MaxChunkSize"/>/<see cref="ChunkingOptions.Overlap"/> have no
/// effect here. See that strategy's remarks for the reasoning and for how to bound chunk size.
/// </remarks>
public sealed class BookChunkingStrategy : IDocumentChunkingStrategy, IChunkingStrategy
{
    private static readonly Regex PageNumberLine =
        new(@"\s+\d+\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly string[] TocHeadings =
        ["table of contents", "contents"];

    private static readonly string[] IndexHeadings =
        ["index"];

    private static readonly string[] ForewordHeadings =
        ["foreword", "preface", "introduction"];

    private readonly HierarchicalMergerChunkingStrategy _inner;
    private readonly BookChunkingOptions _options;

    public BookChunkingStrategy(BookChunkingOptions options)
    {
        _options = options;
        _inner = new HierarchicalMergerChunkingStrategy(new HierarchicalMergerOptions
        {
            MaxDepth = options.MaxDepth,
        });
    }

    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filtered = Filter(sections, cancellationToken);
        var currentChapter = string.Empty;
        await foreach (var chunk in _inner.ChunkDocumentAsync(filtered, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            chunk.Metadata["template"] = "book";
            // Track the top-level (level-1) heading as the chapter for all sub-chunks
            if (chunk.Metadata.TryGetValue("heading_level", out var lvl) && lvl == "1"
                && chunk.Metadata.TryGetValue("heading", out var h))
                currentChapter = h.ToString();
            if (currentChapter.Length > 0)
                chunk.Metadata["chapter"] = currentChapter;
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
        {
            chunk.Metadata["template"] = "book";
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<DocumentSection> Filter(
        IAsyncEnumerable<DocumentSection> sections,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (IsToc(section)) continue;
            if (!_options.IncludeIndex && IsIndex(section)) continue;
            if (!_options.IncludeForeword && IsForeword(section)) continue;
            yield return section;
        }
    }

    private static bool IsToc(DocumentSection section)
    {
        if (section.Heading is { } h &&
            TocHeadings.Any(t => h.Trim().Equals(t, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Heuristic: >50% of non-empty lines end with a page number
        var lines = section.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;
        var pageLines = lines.Count(l => PageNumberLine.IsMatch(l));
        return (double)pageLines / lines.Length > 0.5;
    }

    private static bool IsIndex(DocumentSection section) =>
        section.Heading is { } h &&
        IndexHeadings.Any(i => h.Trim().Equals(i, StringComparison.OrdinalIgnoreCase));

    private static bool IsForeword(DocumentSection section) =>
        section.Heading is { } h &&
        ForewordHeadings.Any(f => h.Trim().Equals(f, StringComparison.OrdinalIgnoreCase));
}
