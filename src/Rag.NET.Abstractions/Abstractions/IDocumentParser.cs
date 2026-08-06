using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Extracts a document's structural sections from a content stream. Multiple parsers may be
/// registered; the ingestion pipeline picks the first whose <see cref="CanParse"/> accepts the
/// document's content type.
/// </summary>
public interface IDocumentParser
{
    /// <summary>Reports whether this parser can handle the given content type (for example a MIME type or file extension, depending on the parser).</summary>
    bool CanParse(string contentType);

    /// <summary>
    /// Extracts sections from <paramref name="stream"/> in document reading order. Does not close
    /// or dispose <paramref name="stream"/>; that is the caller's responsibility.
    /// </summary>
    IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default);
}
