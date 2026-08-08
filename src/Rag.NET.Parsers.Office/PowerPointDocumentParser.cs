using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace Rag.NET.Parsers.PowerPoint;

public sealed class PowerPointDocumentParser : IDocumentParser, IDeclaresContentTypes
{
    private const string PresentationContentType =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [PresentationContentType];

    public bool CanParse(string contentType) =>
        contentType.Equals(PresentationContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = PresentationDocument.Open(stream, false);
        var presentationPart = document.PresentationPart;
        var slideIdList = presentationPart?.Presentation?.SlideIdList;

        if (slideIdList is null)
        {
            yield break;
        }

        var slideIds = slideIdList.Elements<SlideId>();
        int sectionIndex = 0;
        int slideNumber = 0;

        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideNumber++;

            if (slideId.RelationshipId?.Value is null)
            {
                continue;
            }

            var slidePart = (SlidePart)presentationPart!.GetPartById(slideId.RelationshipId.Value);
            var text = ExtractSlideText(slidePart);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new DocumentSection
            {
                Text = text,
                DocumentId = metadata.DocumentId,
                SectionIndex = sectionIndex++,
                PageNumber = slideNumber,
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string ExtractSlideText(SlidePart slidePart)
    {
        var sb = new StringBuilder();

        var slide = slidePart.Slide;
        if (slide is null)
        {
            return string.Empty;
        }

        foreach (var paragraph in slide.Descendants<Drawing.Paragraph>())
        {
            var paragraphText = new StringBuilder();
            foreach (var text in paragraph.Descendants<Drawing.Text>())
            {
                paragraphText.Append(text.Text);
            }

            var line = paragraphText.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString().Trim();
    }
}
