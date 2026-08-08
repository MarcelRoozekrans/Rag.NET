using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace Rag.NET.Parsers.Epub;

public sealed class EpubDocumentParser(HtmlDocumentParser htmlParser) : IDocumentParser, IDeclaresContentTypes
{
    private const string EpubContentType = "application/epub+zip";

    private static readonly EpubReaderOptions s_readerOptions = new(EpubReaderOptionsPreset.RELAXED)
    {
        Epub3NavDocumentReaderOptions = new Epub3NavDocumentReaderOptions(EpubReaderOptionsPreset.RELAXED)
        {
            IgnoreMissingNavManifestItemError = true,
        },
    };

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [EpubContentType];

    public bool CanParse(string contentType) =>
        contentType.Equals(EpubContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var book = await EpubReader.ReadBookAsync(stream, s_readerOptions).ConfigureAwait(false);
        if (book is null)
            yield break;

        int sectionIndex = 0;

#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross yield/await boundaries in async iterators
        foreach (var item in book.ReadingOrder ?? [])
#pragma warning restore HLQ012
        {
            cancellationToken.ThrowIfCancellationRequested();

            var html = item.Content;
            if (string.IsNullOrWhiteSpace(html))
                continue;

            using var chapterStream = new MemoryStream(Encoding.UTF8.GetBytes(html));
            await foreach (var section in htmlParser.ParseAsync(chapterStream, metadata, cancellationToken).ConfigureAwait(false))
            {
                yield return section with { SectionIndex = sectionIndex++ };
            }
        }
    }
}
