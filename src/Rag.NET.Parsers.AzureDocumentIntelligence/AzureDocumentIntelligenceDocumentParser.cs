using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using DocumentSection = Rag.NET.Models.DocumentSection;

namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// Parses documents through Azure AI Document Intelligence: by default TIFF and HEIF scans, which
/// no local parser reads, and opt-in PDF, image, Office and HTML formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sections.</b> Text is built from the result's <c>paragraphs</c>, which every format returns.
/// Paragraphs with bounding regions are grouped by the page of their <i>first</i> region, so a
/// paragraph that crosses a page break appears once, on the page it starts on. Paragraphs
/// without bounding regions, which is how Office and HTML input comes back, are attributed to
/// the page whose <c>spans</c> contain the offset of the paragraph's first span, Azure's page
/// unit for those formats, and group with that page in the same way. One section is emitted per
/// page, in ascending page order, with paragraphs joined by a blank line. A paragraph no page
/// span contains, or any paragraph in a result without page spans, follows as one page-less
/// section each, in document order. A result without paragraphs falls back to each page's
/// lines, then its words.
/// </para>
/// <para>
/// <b>Headings come only from the model.</b> <c>prebuilt-read</c> assigns no paragraph roles, so
/// it yields no headings. A layout model's <c>title</c> and <c>sectionHeading</c> paragraphs set
/// <see cref="DocumentSection.Heading"/> at level 1 and 2 respectively, from the section that
/// starts with that paragraph until the next heading; a heading in the middle of a page applies
/// from the next section on. Page headers, footers and page numbers are dropped, so running page
/// furniture does not repeat into every chunk.
/// </para>
/// <para>
/// <b>Failures throw.</b> A service failure is wrapped in
/// <see cref="AzureDocumentIntelligenceException"/> naming the file; cancellation propagates
/// unwrapped. An empty section list is never returned for a failed call.
/// </para>
/// <para>
/// <b>Spend is recorded, not enforced.</b> The analyzer records analyzed pages to
/// <see cref="ICostLedger"/>, as the PDF OCR engine does, but nothing here checks a budget before
/// submitting a document.
/// </para>
/// <para>
/// <b>Memory: budget for roughly 2.5 to 3.5 times the document per concurrent parse.</b> The
/// document is buffered whole, because the SDK's <see cref="AnalyzeDocumentOptions"/> takes
/// <see cref="BinaryData"/> rather than a <see cref="Stream"/>. A seekable stream is buffered at
/// its exact length; a stream without a length is buffered in a growing array whose capacity
/// can approach twice the document, and that array is what is submitted. The SDK then posts the
/// document base64-encoded inside a JSON body, which adds about 1.33 times the document again
/// while the request is built and sent. Large buffers land on the large object heap.
/// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/> caps the document, not this
/// multiple of it.
/// </para>
/// </remarks>
public sealed class AzureDocumentIntelligenceDocumentParser : IDocumentParser
{
    private const string ParagraphSeparator = "\n\n";

    /// <summary>The chunk size of the bounded copy: the same default <see cref="Stream.CopyToAsync(Stream)"/> uses.</summary>
    private const int CopyChunkSize = 81920;

    private readonly AzureDocumentIntelligenceAnalyzer _analyzer;
    private readonly HashSet<string> _contentTypes;
    private readonly long _maxBytes;

    /// <summary>Creates a parser that analyzes through <paramref name="analyzer"/>.</summary>
    /// <param name="analyzer">The analyzer that submits documents and records their cost.</param>
    /// <param name="options">
    /// Parser options. <see cref="AzureDocumentIntelligenceParserOptions.Formats"/> and
    /// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/> are read once, here; changing
    /// them afterwards does not change what this instance claims or accepts.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="analyzer"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/> is not positive, or is larger
    /// than <see cref="Array.MaxLength"/>, the most one buffer can hold.
    /// </exception>
    public AzureDocumentIntelligenceDocumentParser(
        AzureDocumentIntelligenceAnalyzer analyzer,
        AzureDocumentIntelligenceParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxBytes <= 0 || options.MaxBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxBytes,
                $"{nameof(AzureDocumentIntelligenceParserOptions)}.{nameof(AzureDocumentIntelligenceParserOptions.MaxBytes)} " +
                $"must be between 1 and {Array.MaxLength}, the most one buffer can hold.");
        }

        _analyzer = analyzer;
        _contentTypes = new HashSet<string>(options.Formats.ContentTypes(), StringComparer.OrdinalIgnoreCase);
        _maxBytes = options.MaxBytes;
    }

    /// <summary>
    /// Reports whether <paramref name="contentType"/>'s media type, ignoring case and any
    /// parameters after <c>;</c>, belongs to one of the configured formats.
    /// </summary>
    /// <param name="contentType">A MIME content type, optionally with parameters.</param>
    /// <returns><see langword="true"/> when the parser claims the content type.</returns>
    public bool CanParse(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return _contentTypes.Contains(mediaType);
    }

    /// <inheritdoc/>
    /// <exception cref="AzureDocumentIntelligenceException">
    /// The document is empty or larger than
    /// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/>, in which case nothing was
    /// sent, or the service failed to analyze it.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(metadata);

        var content = await BufferAsync(stream, metadata.FileName, cancellationToken).ConfigureAwait(false);
        var result = await AnalyzeAsync(content, metadata.FileName, cancellationToken).ConfigureAwait(false);

        var sectionIndex = 0;
        foreach (var (text, pageNumber, heading) in MapSections(result))
        {
            yield return new DocumentSection
            {
                Text = text,
                DocumentId = metadata.DocumentId,
                PageNumber = pageNumber,
                Heading = heading?.Text,
                HeadingLevel = heading?.Level,
                SectionIndex = sectionIndex++,
            };
        }
    }

    /// <summary>
    /// Buffers the rest of the stream for upload, leaving the stream open, and refuses a document
    /// larger than the cap. At most <c>_maxBytes + 1</c> bytes are read: the extra byte is what
    /// proves a stream without a length is too large, and it is never written to the buffer, which
    /// therefore holds at most <c>_maxBytes</c>. A seekable stream is also checked by its
    /// remaining length first, so an oversized one is refused without being read. An empty
    /// document is refused too: the service would only answer it with a 400 after the round trip.
    /// </summary>
    private async Task<BinaryData> BufferAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        var remaining = stream.CanSeek ? stream.Length - stream.Position : -1;
        if (remaining > _maxBytes)
        {
            throw TooLarge(fileName);
        }

        // Sized up front when the length is known, so a large scan is not copied on every doubling.
        // The cast is safe: remaining is at most _maxBytes, which the constructor bounds by
        // Array.MaxLength.
        using var buffer = remaining >= 0 ? new MemoryStream((int)remaining) : new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        try
        {
            long total = 0;
            while (true)
            {
                // Never asks for more than one byte past the cap.
                var count = (int)Math.Min(chunk.Length, _maxBytes + 1 - total);
                var read = await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > _maxBytes)
                {
                    throw TooLarge(fileName);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (buffer.Length == 0)
        {
            throw Empty(fileName);
        }

        return new BinaryData(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    private AzureDocumentIntelligenceException TooLarge(string fileName) =>
        new(fileName,
            $"'{fileName}' was not sent to Azure Document Intelligence: it is larger than " +
            $"{nameof(AzureDocumentIntelligenceParserOptions)}.{nameof(AzureDocumentIntelligenceParserOptions.MaxBytes)} " +
            $"({_maxBytes} bytes). The default cap is Azure's limit of 500 MB (500,000,000 bytes) " +
            "on the S0 tier, so a larger file would only be rejected by the service after uploading.");

    private static AzureDocumentIntelligenceException Empty(string fileName) =>
        new(fileName,
            $"'{fileName}' was not sent to Azure Document Intelligence: it is empty, and the " +
            "service rejects a document with no content.");

    /// <summary>
    /// Analyzes the document, translating a service failure into
    /// <see cref="AzureDocumentIntelligenceException"/>. Nothing else is caught, so cancellation
    /// reaches the caller untouched.
    /// </summary>
    private async Task<AnalyzeResult> AnalyzeAsync(BinaryData content, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            return await _analyzer.AnalyzeAsync(content, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception)
        {
            throw new AzureDocumentIntelligenceException(
                fileName,
                $"Azure Document Intelligence could not analyze '{fileName}': {exception.Message}",
                exception);
        }
    }

    /// <summary>Maps a result to section text, page and heading, in emission order.</summary>
    private static List<(string Text, int? PageNumber, Heading? Heading)> MapSections(AnalyzeResult result)
    {
        var paragraphs = result.Paragraphs;
        return paragraphs is null || paragraphs.Count == 0
            ? MapPageContent(result.Pages)
            : MapParagraphs(paragraphs, PageSpanIndex.Create(result.Pages));
    }

    /// <summary>
    /// Groups paragraphs per page and emits page-less ones after them, carrying the heading in
    /// effect at each section's first paragraph. A paragraph's page is that of its first bounding
    /// region, else the page whose spans contain its first span's offset.
    /// </summary>
    private static List<(string Text, int? PageNumber, Heading? Heading)> MapParagraphs(
        IReadOnlyList<DocumentParagraph> paragraphs, PageSpanIndex pageSpans)
    {
        var pages = new SortedDictionary<int, (StringBuilder Text, Heading? Heading)>();
        var pageless = new List<(string Text, int? PageNumber, Heading? Heading)>();
        Heading? current = null;

        foreach (var paragraph in paragraphs)
        {
            if (IsPageFurniture(paragraph) || string.IsNullOrWhiteSpace(paragraph.Content))
            {
                continue;
            }

            if (paragraph.Role == ParagraphRole.Title)
            {
                current = new Heading(paragraph.Content, 1);
            }
            else if (paragraph.Role == ParagraphRole.SectionHeading)
            {
                current = new Heading(paragraph.Content, 2);
            }

            var regions = paragraph.BoundingRegions;
            int pageNumber;
            if (regions is { Count: > 0 })
            {
                pageNumber = regions[0].PageNumber;
            }
            else if (pageSpans.FindPage(paragraph.Spans) is { } spanned)
            {
                pageNumber = spanned;
            }
            else
            {
                pageless.Add((paragraph.Content, null, current));
                continue;
            }

            if (pages.TryGetValue(pageNumber, out var page))
            {
                page.Text.Append(ParagraphSeparator).Append(paragraph.Content);
            }
            else
            {
                pages.Add(pageNumber, (new StringBuilder(paragraph.Content), current));
            }
        }

        var sections = new List<(string Text, int? PageNumber, Heading? Heading)>(pages.Count + pageless.Count);
        foreach (var (pageNumber, page) in pages)
        {
            sections.Add((page.Text.ToString(), pageNumber, page.Heading));
        }

        sections.AddRange(pageless);
        return sections;
    }

    /// <summary>
    /// The fallback for results without paragraphs: one section per page, from its lines, else
    /// its words. Lines are preferred because the service already put them in reading order.
    /// </summary>
    private static List<(string Text, int? PageNumber, Heading? Heading)> MapPageContent(
        IReadOnlyList<DocumentPage>? pages)
    {
        var sections = new List<(string Text, int? PageNumber, Heading? Heading)>();
        if (pages is null)
        {
            return sections;
        }

        var ordered = new List<DocumentPage>(pages);
        ordered.Sort(static (a, b) => a.PageNumber.CompareTo(b.PageNumber));
        var builder = new StringBuilder();
        foreach (var page in ordered)
        {
            builder.Clear();
            AppendPageContent(page, builder);
            if (builder.Length > 0)
            {
                sections.Add((builder.ToString(), page.PageNumber, null));
            }
        }

        return sections;
    }

    /// <summary>Appends a page's lines, one per line, or else its words separated by spaces.</summary>
    private static void AppendPageContent(DocumentPage page, StringBuilder builder)
    {
        var lines = page.Lines;
        if (lines is { Count: > 0 })
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(lines[i].Content);
            }

            return;
        }

        var words = page.Words;
        if (words is null)
        {
            return;
        }

        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(words[i].Content);
        }
    }

    private static bool IsPageFurniture(DocumentParagraph paragraph) =>
        paragraph.Role is { } role
        && (role == ParagraphRole.PageHeader || role == ParagraphRole.PageFooter || role == ParagraphRole.PageNumber);

    /// <summary>
    /// The result's page spans, sorted by offset, so a paragraph without bounding regions can be
    /// placed on the page whose content range holds it.
    /// </summary>
    private sealed class PageSpanIndex
    {
        private readonly (int Offset, int End, int PageNumber)[] _spans;

        private PageSpanIndex((int Offset, int End, int PageNumber)[] spans) => _spans = spans;

        public static PageSpanIndex Create(IReadOnlyList<DocumentPage>? pages)
        {
            var spans = new List<(int Offset, int End, int PageNumber)>();
            if (pages is not null)
            {
                foreach (var page in pages)
                {
                    if (page.Spans is null)
                    {
                        continue;
                    }

                    foreach (var span in page.Spans)
                    {
                        if (span.Length > 0)
                        {
                            spans.Add((span.Offset, span.Offset + span.Length, page.PageNumber));
                        }
                    }
                }
            }

            spans.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
            return new PageSpanIndex([.. spans]);
        }

        /// <summary>
        /// The page whose span range <c>[offset, offset + length)</c> contains the offset of
        /// <paramref name="paragraphSpans"/>' first span, or <see langword="null"/> when the
        /// paragraph has no span or no page span contains it.
        /// </summary>
        public int? FindPage(IReadOnlyList<DocumentSpan>? paragraphSpans)
        {
            if (paragraphSpans is not { Count: > 0 } || _spans.Length == 0)
            {
                return null;
            }

            var offset = paragraphSpans[0].Offset;

            // The last page span starting at or before the offset is the only one that can hold
            // it, because a page's spans do not overlap another page's.
            int low = 0;
            int high = _spans.Length - 1;
            int candidate = -1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                if (_spans[middle].Offset <= offset)
                {
                    candidate = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return candidate >= 0 && offset < _spans[candidate].End ? _spans[candidate].PageNumber : null;
        }
    }

    /// <summary>A heading paragraph's text and its level: 1 for a title, 2 for a section heading.</summary>
    private sealed record Heading(string Text, int Level);
}
