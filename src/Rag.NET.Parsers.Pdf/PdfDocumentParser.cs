using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf.Ocr;
using Rag.NET.Parsers.Pdf.TableExtraction;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Rag.NET.Parsers.Pdf;

public sealed class PdfDocumentParser : IDocumentParser, IDisposable, IDeclaresContentTypes
{
    private const string PdfContentType = "application/pdf";

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [PdfContentType];

    private readonly PdfParserOptions _options;
    private readonly ILogger<PdfDocumentParser>? _logger;
    private readonly IPdfOcrEngine? _ocrEngine;
    private readonly IDocumentOcrEngine? _documentOcrEngine;

    // Tesseract engines are NOT thread-safe. The parser is a DI singleton and each engine
    // lives for the parser's lifetime, while documents parse in parallel (the Phase 1.3
    // batch ingestion optimiser) — so every Recognize call is serialized through this lock.
    //
    // The document-level engine deliberately does NOT take this lock. It is a network client
    // with no shared native state; serializing it would gut throughput on exactly the
    // workload this parser is optimised for — and `lock` cannot span `await` regardless.
    private readonly Lock _ocrLock = new();

    /// <param name="options">Parser options; defaults are used when omitted.</param>
    /// <param name="logger">Optional logger for the degraded-path warnings.</param>
    /// <param name="documentOcrEngine">
    /// Optional whole-PDF OCR engine. Supplying one selects the document-level OCR path;
    /// the per-image Tesseract fallback is then never constructed. Combining it with
    /// <see cref="PdfParserOptions.UseOcrFallback"/> is rejected at registration time by the
    /// builder extensions (see <see cref="PdfParserBuilderExtensions"/>); that guard is
    /// DI-only, so a direct-construction caller can still express the combination — in which
    /// case the document engine wins and the <c>EnableOcr</c> gate check that
    /// <c>UseOcrFallback</c> would otherwise trigger is bypassed.
    /// </param>
    public PdfDocumentParser(
        PdfParserOptions? options = null,
        ILogger<PdfDocumentParser>? logger = null,
        IDocumentOcrEngine? documentOcrEngine = null)
        : this(options, logger, ocrEngine: null, documentOcrEngine)
    {
    }

    /// <summary>Test seam: a non-null <paramref name="ocrEngine"/> replaces the gated factory engine.</summary>
    internal PdfDocumentParser(
        PdfParserOptions? options, ILogger<PdfDocumentParser>? logger, IPdfOcrEngine? ocrEngine)
        : this(options, logger, ocrEngine, documentOcrEngine: null)
    {
    }

    private PdfDocumentParser(
        PdfParserOptions? options,
        ILogger<PdfDocumentParser>? logger,
        IPdfOcrEngine? ocrEngine,
        IDocumentOcrEngine? documentOcrEngine)
    {
        _options = options ?? new PdfParserOptions();
        _logger = logger;
        _documentOcrEngine = documentOcrEngine;
        // Fail fast: in a gate-off compilation the stub engine's constructor throws the
        // instructive misconfiguration error here, at parser construction — not at the
        // first OCR-needed page. A document-level engine skips that factory entirely: it is
        // ungated, and it takes precedence. The builder extensions reject the combination at
        // registration time, so under DI only one engine is ever supplied; a direct-
        // construction caller that passes both gets the document engine and no gate check.
        _ocrEngine = documentOcrEngine is not null
            ? null
            : ocrEngine ?? (_options.UseOcrFallback ? PdfOcrEngineFactory.Create(_options) : null);
    }

    /// <summary>
    /// Disposes the OCR engine when one was created (only in <c>EnableOcr</c> builds the
    /// engine holds native Tesseract resources). The DI container disposes singleton
    /// parsers at shutdown; direct-construction users should dispose explicitly.
    /// </summary>
    public void Dispose() => (_ocrEngine as IDisposable)?.Dispose();

    public bool CanParse(string contentType) =>
        contentType.Equals(PdfContentType, StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default) =>
        _documentOcrEngine is { } documentEngine
            ? ParseWithDocumentOcrAsync(stream, metadata, documentEngine, cancellationToken)
            : ParsePagesAsync(stream, metadata, cancellationToken);

    /// <summary>
    /// The default path (no document-level engine): PdfPig streams pages from the caller's
    /// stream and each page is parsed independently, the per-image OCR fallback included.
    /// </summary>
    private async IAsyncEnumerable<DocumentSection> ParsePagesAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

    /// <summary>
    /// The document-level OCR path. PdfPig parses first, as always; the engine is called
    /// <b>once</b> for the whole document, and only if some page came back below
    /// <see cref="PdfParserOptions.OcrMinCharacters"/> — a document PdfPig read in full
    /// never reaches a per-page priced API.
    /// </summary>
    private async IAsyncEnumerable<DocumentSection> ParseWithDocumentOcrAsync(
        Stream stream,
        DocumentMetadata metadata,
        IDocumentOcrEngine engine,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pdf = await BufferAsync(stream, cancellationToken).ConfigureAwait(false);
        using var document = PdfDocument.Open(pdf);
        int pageCount = document.NumberOfPages;

        IReadOnlyDictionary<int, string>? recognized = null;
        bool ocrDecided = false;
        int sectionIndex = 0;
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ocrDecided && page.Text.Length < _options.OcrMinCharacters)
            {
                // Exactly one call per document, made the moment the first sub-threshold
                // page appears — never one per page, and never at all without one.
                ocrDecided = true;
                recognized = await RecognizeDocumentAsync(engine, pdf, pageCount, cancellationToken)
                    .ConfigureAwait(false);
            }

            var sections = DocumentOcrSections(page, metadata, recognized);
            // Index loop: spans (CollectionsMarshal.AsSpan) cannot cross yield boundaries.
            for (int i = 0; i < sections.Count; i++)
            {
                yield return sections[i] with { SectionIndex = sectionIndex++ };
            }
        }
    }

    /// <summary>
    /// Copies the PDF into memory so PdfPig and the document-level engine each get their own
    /// view of it. PdfPig reads its stream lazily throughout <c>GetPages()</c>, so seeking
    /// that same stream mid-parse to feed the engine would reposition it underneath an active
    /// parse; buffering once up front is the only interleaving-safe arrangement.
    /// <para>
    /// Non-seekable streams are <b>buffered, not refused</b>. Two reasons. PdfPig accepts a
    /// non-seekable stream on the default path, so buffering keeps both paths behaviourally
    /// identical instead of making the document-level path reject input the parser otherwise
    /// handles. And the repo's <c>RagError.NonSeekableStream</c> precedent lives in the
    /// ingestor's pre-flight check, which can return a <c>RagError</c>;
    /// <c>IDocumentParser.ParseAsync</c> yields sections and has no such channel, so refusing
    /// here would mean throwing — against the parser's degraded-never-broken posture.
    /// </para>
    /// <para>
    /// Seekable streams are rewound to 0 (where PdfPig reads from regardless of the incoming
    /// position) and read in one allocation of the exact length, so the peak is one copy
    /// rather than a growing <see cref="MemoryStream"/> plus its <c>ToArray</c>. That matters:
    /// this is the one path that can legitimately be handed a very large PDF.
    /// </para>
    /// </summary>
    private static async ValueTask<byte[]> BufferAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
            long length = stream.Length;
            if (length > 0 && length <= Array.MaxLength)
            {
                var exact = new byte[(int)length];
                await stream.ReadExactlyAsync(exact, cancellationToken).ConfigureAwait(false);
                return exact;
            }
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// The one document-level OCR call. Returns <see langword="null"/> — meaning "every page
    /// keeps the text PdfPig extracted" — when the page cap forbids the call or the engine
    /// fails; both are warnings, never exceptions. Cancellation is not a degradation and
    /// propagates to the caller.
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<int, string>?> RecognizeDocumentAsync(
        IDocumentOcrEngine engine, byte[] pdf, int pageCount, CancellationToken cancellationToken)
    {
        if (pageCount > _options.MaxOcrPages)
        {
            // Document-level providers bill every page of the submitted document, so this
            // cap is what stands between one large PDF and a large invoice.
            if (_logger is not null)
            {
                PdfParserLog.DocumentOcrPageCapExceeded(_logger, pageCount, _options.MaxOcrPages);
            }

            return null;
        }

        try
        {
            // No _ocrLock: see the field comment. This engine is a network client.
            using var pdfStream = new MemoryStream(pdf, writable: false);
            var result = await engine.RecognizeAsync(pdfStream, cancellationToken).ConfigureAwait(false);
            return result.PageText;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_logger is not null)
            {
                PdfParserLog.DocumentOcrFailed(_logger, exception);
            }

            return null;
        }
    }

    /// <summary>
    /// Picks the text for one page on the document-level path. Recognized text replaces only
    /// the pages PdfPig read below <see cref="PdfParserOptions.OcrMinCharacters"/>; pages
    /// PdfPig read successfully keep PdfPig's text, which is exact — discarding it would
    /// trade accuracy for nothing. Lossless in every degraded case: a capped, failed or
    /// silent OCR leaves the page exactly as it would be with no engine configured.
    /// </summary>
    private List<DocumentSection> DocumentOcrSections(
        Page page, DocumentMetadata metadata, IReadOnlyDictionary<int, string>? recognized)
    {
        if (page.Text.Length >= _options.OcrMinCharacters || recognized is null)
        {
            return NonOcrSections(page, metadata);
        }

        if (!recognized.TryGetValue(page.Number, out var text) || string.IsNullOrWhiteSpace(text))
        {
            if (_logger is not null)
            {
                PdfParserLog.DocumentOcrNoText(_logger, page.Number);
            }

            return NonOcrSections(page, metadata);
        }

        return
        [
            new DocumentSection
            {
                Text = text,
                Heading = "ocr",
                DocumentId = metadata.DocumentId,
                PageNumber = page.Number,
            },
        ];
    }

    private List<DocumentSection> ParsePage(Page page, DocumentMetadata metadata)
    {
        if (_options.UseOcrFallback
            && _ocrEngine is { } engine
            && page.Text.Length < _options.OcrMinCharacters)
        {
            return OcrSections(engine, page, metadata);
        }

        return NonOcrSections(page, metadata);
    }

    /// <summary>Table-aware parsing where enabled, with the plain-text path as its fallback.</summary>
    private List<DocumentSection> NonOcrSections(Page page, DocumentMetadata metadata)
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

    /// <summary>
    /// OCR fallback for a page whose extracted text is below
    /// <see cref="PdfParserOptions.OcrMinCharacters"/> (scanned pages are full-page embedded
    /// images with no text layer): runs the engine over the page's embedded images,
    /// largest display area first, until one yields text. Degraded, never broken — and
    /// lossless: no images, no recognized text, or an engine failure logs a warning and
    /// falls back to the plain-text path, so short-but-real extracted text is never lost by
    /// enabling OCR; genuinely empty pages still emit nothing (today's empty-page behavior).
    /// Vector-only pages (no embedded images) cannot be OCR-ed without a rasterizer and
    /// take the same plain-text fallback.
    /// </summary>
    private List<DocumentSection> OcrSections(IPdfOcrEngine engine, Page page, DocumentMetadata metadata)
    {
        try
        {
            var images = ImagesLargestFirst(page);
            if (images.Count == 0)
            {
                if (_logger is not null)
                {
                    PdfParserLog.OcrNoImages(_logger, page.Number);
                }

                return PlainTextSections(page, metadata);
            }

            for (int i = 0; i < images.Count; i++)
            {
                var text = RecognizeImage(engine, images[i]);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return
                    [
                        new DocumentSection
                        {
                            Text = text,
                            Heading = "ocr",
                            DocumentId = metadata.DocumentId,
                            PageNumber = page.Number,
                        },
                    ];
                }
            }

            if (_logger is not null)
            {
                PdfParserLog.OcrNoText(_logger, page.Number);
            }

            return PlainTextSections(page, metadata);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_logger is not null)
            {
                PdfParserLog.OcrFailed(_logger, page.Number, exception);
            }

            return PlainTextSections(page, metadata);
        }
    }

    private string? RecognizeImage(IPdfOcrEngine engine, IPdfImage image)
    {
        // Prefer PNG bytes (PdfPig re-encodes decodable images, e.g. flate bitmaps); fall
        // back to the raw embedded stream, which for DCT-filtered images is the JPEG file
        // itself — a format the OCR engine can load directly.
        var bytes = image.TryGetPng(out var png) ? png : image.RawMemory.ToArray();
        lock (_ocrLock)
        {
            return engine.Recognize(bytes);
        }
    }

    private static List<IPdfImage> ImagesLargestFirst(Page page)
    {
        var images = new List<IPdfImage>(page.GetImages());
        images.Sort(static (a, b) =>
            (b.BoundingBox.Width * b.BoundingBox.Height).CompareTo(a.BoundingBox.Width * a.BoundingBox.Height));
        return images;
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
