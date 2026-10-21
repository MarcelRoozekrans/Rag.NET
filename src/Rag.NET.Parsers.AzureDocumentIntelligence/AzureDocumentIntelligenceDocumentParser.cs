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
/// paragraph that crosses a page break appears once, on the page it starts on; one section is
/// emitted per page, in ascending page order, with paragraphs joined by a blank line. Paragraphs
/// without bounding regions (Office and HTML input) follow as one page-less section each, in
/// document order. A result without paragraphs falls back to each page's lines, then its words.
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
    /// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/> is not positive.
    /// </exception>
    public AzureDocumentIntelligenceDocumentParser(
        AzureDocumentIntelligenceAnalyzer analyzer,
        AzureDocumentIntelligenceParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxBytes,
                $"{nameof(AzureDocumentIntelligenceParserOptions)}.{nameof(AzureDocumentIntelligenceParserOptions.MaxBytes)} " +
                "must be positive.");
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
    /// The document is larger than <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/>, in
    /// which case nothing was sent, or the service failed to analyze it.
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
    /// proves a stream without a length is too large. A seekable stream is also checked by its
    /// remaining length first, so an oversized one is refused without being read.
    /// </summary>
    private async Task<BinaryData> BufferAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        var remaining = stream.CanSeek ? stream.Length - stream.Position : -1;
        if (remaining > _maxBytes)
        {
            throw TooLarge(fileName);
        }

        // Sized up front when the length is known, so a large scan is not copied on every doubling.
        using var buffer = remaining >= 0
            ? new MemoryStream((int)Math.Min(remaining, Array.MaxLength))
            : new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        try
        {
            long total = 0;
            while (total <= _maxBytes)
            {
                // Never asks for more than one byte past the cap. Written as Min(...) + 1 rather
                // than Min(..., _maxBytes + 1 - total) so a cap of long.MaxValue cannot overflow.
                var count = (int)Math.Min(chunk.Length - 1L, _maxBytes - total) + 1;
                var read = await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                total += read;
            }

            if (total > _maxBytes)
            {
                throw TooLarge(fileName);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return new BinaryData(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    private AzureDocumentIntelligenceException TooLarge(string fileName) =>
        new(fileName,
            $"'{fileName}' was not sent to Azure Document Intelligence: it is larger than " +
            $"{nameof(AzureDocumentIntelligenceParserOptions)}.{nameof(AzureDocumentIntelligenceParserOptions.MaxBytes)} " +
            $"({_maxBytes} bytes). The default cap is Azure's limit of 500 MB on the S0 tier, so a " +
            "larger file would only be rejected by the service after uploading.");

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
            : MapParagraphs(paragraphs);
    }

    /// <summary>
    /// Groups paged paragraphs per page and emits page-less ones after them, carrying the heading
    /// in effect at each section's first paragraph.
    /// </summary>
    private static List<(string Text, int? PageNumber, Heading? Heading)> MapParagraphs(
        IReadOnlyList<DocumentParagraph> paragraphs)
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
            if (regions is null || regions.Count == 0)
            {
                pageless.Add((paragraph.Content, null, current));
                continue;
            }

            var pageNumber = regions[0].PageNumber;
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

    /// <summary>A heading paragraph's text and its level: 1 for a title, 2 for a section heading.</summary>
    private sealed record Heading(string Text, int Level);
}
