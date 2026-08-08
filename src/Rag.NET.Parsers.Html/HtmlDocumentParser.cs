using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Html;

public sealed class HtmlDocumentParser : IDocumentParser, IDeclaresContentTypes
{
    private const string HtmlContentType = "text/html";

    private static readonly HashSet<string> s_headingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6",
    };

    private static readonly string s_removeSelector = string.Join(", ", new[] { "script", "style", "nav", "footer", "header" });

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [HtmlContentType];

    public bool CanParse(string contentType) =>
        contentType.Equals(HtmlContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(stream, cancellationToken).ConfigureAwait(false);

        RemoveNonContentElements(document);
        ConvertLinksToTextUrl(document);

        var body = document.Body;
        if (body is null)
        {
            yield break;
        }

        var headings = body.QuerySelectorAll("h1, h2, h3, h4, h5, h6").ToList();

        if (headings.Count == 0)
        {
            var text = GetCleanText(body);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return CreateSection(text, metadata.DocumentId, 0);
            }

            yield break;
        }

        for (int i = 0; i < headings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var section = BuildHeadingSection(headings[i], metadata.DocumentId, i);
            if (section is not null)
            {
                yield return section;
            }
        }
    }

    private static void RemoveNonContentElements(IDocument document)
    {
        var elements = document.QuerySelectorAll(s_removeSelector).ToList();
        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].Remove();
        }
    }

    private static void ConvertLinksToTextUrl(IDocument document)
    {
        var links = document.QuerySelectorAll("a[href]").ToList();
        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var href = link.GetAttribute("href");
            var text = link.TextContent.Trim();
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(href))
            {
                link.TextContent = $"{text} ({href})";
            }
        }
    }

    private static DocumentSection? BuildHeadingSection(IElement heading, DocumentId documentId, int sectionIndex)
    {
        var headingText = heading.TextContent.Trim();
        var sectionContent = new StringBuilder();
        sectionContent.AppendLine(headingText);

        var sibling = heading.NextElementSibling;
        while (sibling is not null && !s_headingTags.Contains(sibling.TagName))
        {
            var siblingText = GetCleanText(sibling);
            if (!string.IsNullOrWhiteSpace(siblingText))
            {
                sectionContent.AppendLine(siblingText);
            }

            sibling = sibling.NextElementSibling;
        }

        var finalText = sectionContent.ToString().Trim();
        if (string.IsNullOrWhiteSpace(finalText))
        {
            return null;
        }

        return new DocumentSection
        {
            Text = finalText,
            DocumentId = documentId,
            SectionIndex = sectionIndex,
            Heading = headingText,
            HeadingLevel = heading.TagName[1] - '0',
        };
    }

    private static DocumentSection CreateSection(string text, DocumentId documentId, int sectionIndex) =>
        new()
        {
            Text = text,
            DocumentId = documentId,
            SectionIndex = sectionIndex,
        };

    private static string GetCleanText(IElement element)
    {
        var text = element.TextContent;
        return string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }
}
