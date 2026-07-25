using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf.TableExtraction;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Rag.NET.Parsers.Pdf;

public sealed class PdfDocumentParser : IDocumentParser
{
    private readonly PdfParserOptions _options;
    private readonly ILogger<PdfDocumentParser>? _logger;

    public PdfDocumentParser(PdfParserOptions? options = null, ILogger<PdfDocumentParser>? logger = null)
    {
        _options = options ?? new PdfParserOptions();
        _logger = logger;
    }

    public bool CanParse(string contentType) =>
        contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(stream);

        int sectionIndex = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sections = ParsePage(page, metadata);
            // Index loop: spans (CollectionsMarshal.AsSpan) cannot cross yield boundaries.
            for (int i = 0; i < sections.Count; i++)
            {
                yield return sections[i] with { SectionIndex = sectionIndex++ };
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private List<DocumentSection> ParsePage(Page page, DocumentMetadata metadata)
    {
        if (!_options.ExtractTables)
        {
            return PlainTextSections(page, metadata);
        }

        try
        {
            return TableAwareSections(page, metadata);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Degraded, never broken: any extractor failure falls back to today's
            // whole-page plain-text behavior.
            if (_logger is not null)
            {
                PdfParserLog.TableExtractionFailed(_logger, page.Number, exception);
            }

            return PlainTextSections(page, metadata);
        }
    }

    /// <summary>The pre-table-extraction behavior: one <c>page.Text</c> section per non-empty page.</summary>
    private static List<DocumentSection> PlainTextSections(Page page, DocumentMetadata metadata)
    {
        var text = page.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return
        [
            new DocumentSection
            {
                Text = text,
                DocumentId = metadata.DocumentId,
                PageNumber = page.Number,
            },
        ];
    }

    private List<DocumentSection> TableAwareSections(Page page, DocumentMetadata metadata)
    {
        var words = NormalizeWords(page);
        var (tables, proseWords) = PdfTableExtractor.Extract(words, _options);
        if (tables.Count == 0)
        {
            // No tables on this page: keep the exact legacy page.Text output.
            return PlainTextSections(page, metadata);
        }

        // Prose (space-joined leftover words in reading order) and table sections are
        // ordered by their top Y so SectionIndex follows the on-page document order.
        var ordered = new List<(double TopY, DocumentSection Section)>();
        var proseText = JoinWords(proseWords);
        if (proseText.Length > 0)
        {
            ordered.Add((proseWords[0].Y, new DocumentSection
            {
                Text = proseText,
                DocumentId = metadata.DocumentId,
                PageNumber = page.Number,
            }));
        }

        for (int i = 0; i < tables.Count; i++)
        {
            ordered.Add((tables[i].TopY, new DocumentSection
            {
                Text = PdfTableExtractor.RenderMarkdown(tables[i]),
                Heading = "table",
                DocumentId = metadata.DocumentId,
                PageNumber = page.Number,
            }));
        }

        ordered.Sort(static (a, b) => a.TopY.CompareTo(b.TopY));
        var sections = new List<DocumentSection>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            sections.Add(ordered[i].Section);
        }

        return sections;
    }

    private static List<WordBox> NormalizeWords(Page page)
    {
        // PdfPig's Y axis is bottom-up (origin at the page's bottom-left); the clustering
        // core expects top-down rows, so Y is re-based to (page height - bounding box top).
        var words = new List<WordBox>();
        foreach (var word in page.GetWords())
        {
            var box = word.BoundingBox;
            words.Add(new WordBox(word.Text, box.Left, page.Height - box.Top, box.Width, box.Height));
        }

        return words;
    }

    private static string JoinWords(IReadOnlyList<WordBox> words)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(words[i].Text);
        }

        return builder.ToString().Trim();
    }
}
