using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Rag.NET.Models;

namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Splits a section's text at heading lines, so the templates work on documents whose parser
/// produced no structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/636">#636</see>.
/// <c>HierarchicalMergerChunkingStrategy</c> <b>merges</b> sections — it classifies each incoming
/// <see cref="DocumentSection"/> with a level and folds the rest into the current chunk. It never
/// splits a section's text. <c>TextDocumentParser</c> yields <b>one</b> section for a whole file,
/// so the Legal and Book templates returned exactly one chunk for any plain-text document, for any
/// pattern. They did not fail; they silently did nothing.
/// </para>
/// <para>
/// <b>It passes through whatever the parser already structured.</b> A section carrying a
/// <see cref="DocumentSection.Heading"/> is emitted unchanged — markdown and HTML already arrive
/// split, and re-splitting them would cut inside content the parser deliberately kept together.
/// A section whose text matches no pattern is also emitted unchanged. So this is additive: nothing
/// that worked before changes, and plain text starts working.
/// </para>
/// <para>
/// <b>Why in the templates rather than in the merger.</b> The merger's contract is merging, and
/// every other caller relies on it leaving section boundaries alone. The templates are the ones
/// claiming to understand clauses and chapters, so the understanding belongs to them.
/// </para>
/// </remarks>
internal static class HeadingTextSplitter
{
    /// <summary>Splits each unstructured section at its heading lines.</summary>
    /// <param name="sections">The parser's sections.</param>
    /// <param name="patterns">Heading patterns, outermost level first.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The sections, with unstructured ones split at their headings.</returns>
    public static async IAsyncEnumerable<DocumentSection> SplitAsync(
        IAsyncEnumerable<DocumentSection> sections,
        Regex[] patterns,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(patterns);

        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (patterns.Length == 0 || !string.IsNullOrEmpty(section.Heading))
            {
                yield return section;
                continue;
            }

            var split = Split(section, patterns);
            if (split.Count <= 1)
            {
                yield return section;
                continue;
            }

            // Indexed rather than foreach: HLQ012 forbids enumerating a List<T> here, and
            // CollectionsMarshal.AsSpan cannot be used in an iterator because a ref struct may not
            // live across a yield.
            for (var i = 0; i < split.Count; i++)
            {
                yield return split[i];
            }
        }
    }

    /// <summary>Cuts one section's text at every heading line it contains.</summary>
    /// <param name="section">The unstructured section.</param>
    /// <param name="patterns">Heading patterns, outermost level first.</param>
    /// <returns>One section per heading run, or a single-element list when nothing matched.</returns>
    /// <remarks>
    /// The first run is emitted even when the document opens with body text before any heading —
    /// dropping it would lose a preamble, which in a licence is the grant everything else qualifies.
    /// </remarks>
    private static List<DocumentSection> Split(DocumentSection section, Regex[] patterns)
    {
        var pieces = new List<DocumentSection>();
        var buffer = new StringBuilder();
        string? heading = null;
        var level = 0;
        var index = section.SectionIndex;

        foreach (var line in section.Text.Split('\n'))
        {
            var matched = LevelOf(line, patterns);
            if (matched is null)
            {
                _ = buffer.Append(line).Append('\n');
                continue;
            }

            Flush(pieces, section, ref buffer, heading, level, ref index);
            heading = line.Trim();
            level = matched.Value;
            _ = buffer.Append(line).Append('\n');
        }

        Flush(pieces, section, ref buffer, heading, level, ref index);
        return pieces;
    }

    /// <summary>Emits the buffered run as a section when it holds anything.</summary>
    private static void Flush(
        List<DocumentSection> pieces,
        DocumentSection section,
        ref StringBuilder buffer,
        string? heading,
        int level,
        ref int index)
    {
        var text = buffer.ToString().Trim();
        buffer = new StringBuilder();

        if (text.Length == 0)
        {
            return;
        }

        // The heading and its body are emitted as TWO sections, and that is load-bearing.
        // HierarchicalMergerChunkingStrategy clears its buffer when it recognises a heading and
        // folds only later, non-heading sections into the chunk. A single section carrying both
        // therefore loses the body: the chunk comes out as the heading line alone. That is exactly
        // what happened on the first attempt here, and it was invisible until an assertion looked
        // for body text rather than for words that appear in headings.
        if (heading is not null)
        {
            pieces.Add(new DocumentSection
            {
                Text = heading,
                DocumentId = section.DocumentId,
                Heading = heading,
                HeadingLevel = level,
                PageNumber = section.PageNumber,
                SectionIndex = index++,
            });

            var body = text.Length > heading.Length ? text[heading.Length..].Trim() : string.Empty;
            if (body.Length == 0)
            {
                return;
            }

            text = body;
        }

        pieces.Add(new DocumentSection
        {
            Text = text,
            DocumentId = section.DocumentId,
            Heading = null,
            HeadingLevel = null,
            PageNumber = section.PageNumber,
            SectionIndex = index++,
        });
    }

    /// <summary>The one-based level of the first pattern this line matches, or null.</summary>
    private static int? LevelOf(string line, Regex[] patterns)
    {
        for (var i = 0; i < patterns.Length; i++)
        {
            if (patterns[i].IsMatch(line))
            {
                return i + 1;
            }
        }

        return null;
    }
}
