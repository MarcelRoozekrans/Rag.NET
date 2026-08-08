using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Word;

public sealed class WordDocumentParser : IDocumentParser, IDeclaresContentTypes
{
    private const string WordContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly Dictionary<string, int> s_headingStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Heading1"] = 1,
        ["Heading2"] = 2,
        ["Heading3"] = 3,
        ["Heading4"] = 4,
        ["Heading5"] = 5,
        ["Heading6"] = 6,
    };

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [WordContentType];

    public bool CanParse(string contentType) =>
        contentType.Equals(WordContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body;

        if (body is null)
        {
            yield break;
        }

        var paragraphs = body.Elements<Paragraph>().ToList();
        if (paragraphs.Count == 0)
        {
            yield break;
        }

        bool hasHeadings = paragraphs.Any(p => GetHeadingLevel(p) is not null);

        if (!hasHeadings)
        {
            var section = BuildFlatSection(paragraphs, metadata);
            if (section is not null)
            {
                yield return section;
            }

            yield break;
        }

        await foreach (var section in ProcessWithHeadings(paragraphs, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section;
        }
    }

    private static DocumentSection? BuildFlatSection(List<Paragraph> paragraphs, DocumentMetadata metadata)
    {
        var allText = GetAllText(paragraphs);
        if (string.IsNullOrWhiteSpace(allText))
        {
            return null;
        }

        return new DocumentSection
        {
            Text = allText,
            DocumentId = metadata.DocumentId,
            SectionIndex = 0,
        };
    }

    private static async IAsyncEnumerable<DocumentSection> ProcessWithHeadings(
        List<Paragraph> paragraphs,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int sectionIndex = 0;
        string? currentHeading = null;
        int? currentHeadingLevel = null;
        var currentContent = new StringBuilder();

        for (int i = 0; i < paragraphs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var headingLevel = GetHeadingLevel(paragraphs[i]);
            var text = paragraphs[i].InnerText.Trim();

            if (headingLevel is not null)
            {
                var section = EmitSection(currentHeading, currentHeadingLevel, currentContent, metadata, ref sectionIndex);
                if (section is not null)
                {
                    yield return section;
                }

                currentHeading = text;
                currentHeadingLevel = headingLevel;
                currentContent.Clear();
                currentContent.AppendLine(text);
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                currentContent.AppendLine(text);
            }
        }

        // Emit last section
        var lastSection = EmitSection(currentHeading, currentHeadingLevel, currentContent, metadata, ref sectionIndex);
        if (lastSection is not null)
        {
            yield return lastSection;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static DocumentSection? EmitSection(
        string? heading,
        int? headingLevel,
        StringBuilder content,
        DocumentMetadata metadata,
        ref int sectionIndex)
    {
        if (heading is null)
        {
            return null;
        }

        var sectionText = content.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sectionText))
        {
            return null;
        }

        return new DocumentSection
        {
            Text = sectionText,
            DocumentId = metadata.DocumentId,
            SectionIndex = sectionIndex++,
            Heading = heading,
            HeadingLevel = headingLevel,
        };
    }

    private static int? GetHeadingLevel(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is not null && s_headingStyles.TryGetValue(styleId, out var level))
        {
            return level;
        }

        return null;
    }

    private static string GetAllText(List<Paragraph> paragraphs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            var text = paragraphs[i].InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
            }
        }

        return sb.ToString().Trim();
    }
}
