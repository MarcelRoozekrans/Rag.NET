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

        // Document order: prose words are partitioned into segments above/between/below the
        // detected tables (by table top Y) and emitted interleaved with the table sections,
        // so SectionIndex follows the on-page reading order.
        var segments = PartitionProse(proseWords, tables);
        var sections = new List<DocumentSection>();
        for (int t = 0; t <= tables.Count; t++)
        {
            AddProseSection(sections, segments[t], metadata, page.Number);
            if (t < tables.Count)
            {
                sections.Add(new DocumentSection
                {
                    Text = PdfTableExtractor.RenderMarkdown(tables[t]),
                    Heading = "table",
                    DocumentId = metadata.DocumentId,
                    PageNumber = page.Number,
                });
            }
        }

        return sections;
    }

    /// <summary>
    /// Splits prose words into (tables.Count + 1) segments: segment 0 is above the first
    /// table, segment i sits between table i-1 and table i, the last segment is below the
    /// final table. Tables arrive in top-down order; prose words keep their reading order.
    /// </summary>
    private static List<List<WordBox>> PartitionProse(
        IReadOnlyList<WordBox> proseWords, IReadOnlyList<DetectedTable> tables)
    {
        var segments = new List<List<WordBox>>(tables.Count + 1);
        for (int i = 0; i <= tables.Count; i++)
        {
            segments.Add([]);
        }

        for (int i = 0; i < proseWords.Count; i++)
        {
            int segment = 0;
            while (segment < tables.Count && proseWords[i].Y > tables[segment].TopY)
            {
                segment++;
            }

            segments[segment].Add(proseWords[i]);
        }

        return segments;
    }

    private static void AddProseSection(
        List<DocumentSection> sections, IReadOnlyList<WordBox> segment,
        DocumentMetadata metadata, int pageNumber)
    {
        var text = JoinWords(segment);
        if (text.Length == 0)
        {
            return;
        }

        sections.Add(new DocumentSection
        {
            Text = text,
            DocumentId = metadata.DocumentId,
            PageNumber = pageNumber,
        });
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
