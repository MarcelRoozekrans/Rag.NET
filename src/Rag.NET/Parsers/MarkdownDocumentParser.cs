using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers;

public sealed partial class MarkdownDocumentParser : IDocumentParser
{
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex HeadingRegex();

    public bool CanParse(string contentType) =>
        contentType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("text/x-markdown", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var matches = HeadingRegex().Matches(text);

        if (matches.Count == 0)
        {
            yield return CreatePlainSection(text.Trim(), metadata.DocumentId, 0);
            yield break;
        }

        int sectionIndex = 0;

        // Content before first heading
        if (matches[0].Index > 0)
        {
            var preText = text[..matches[0].Index].Trim();
            if (preText.Length > 0)
            {
                yield return CreatePlainSection(preText, metadata.DocumentId, sectionIndex++);
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateHeadingSection(text, matches, i, metadata.DocumentId, sectionIndex++);
        }
    }

    private static DocumentSection CreatePlainSection(string text, string documentId, int index) =>
        new()
        {
            Text = text,
            DocumentId = documentId,
            SectionIndex = index
        };

    private static DocumentSection CreateHeadingSection(
        string text, MatchCollection matches, int i, string documentId, int sectionIndex)
    {
        var match = matches[i];
        int contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
        var sectionText = text[match.Index..contentEnd].Trim();

        // Count '#' characters for heading level - groups won't capture with ExplicitCapture
        int headingLevel = 0;
        foreach (char c in match.Value)
        {
            if (c == '#')
                headingLevel++;
            else
                break;
        }

        // Extract heading text after "# "
        string heading = match.Value[(headingLevel + 1)..].Trim();

        return new DocumentSection
        {
            Text = sectionText,
            DocumentId = documentId,
            SectionIndex = sectionIndex,
            HeadingLevel = headingLevel,
            Heading = heading
        };
    }
}
