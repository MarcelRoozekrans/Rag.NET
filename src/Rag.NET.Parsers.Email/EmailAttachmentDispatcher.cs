using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Shared attachment dispatch for the email parsers: selects the first registered parser — skipping the
/// dispatching parser itself via <see cref="ReferenceEquals(object?, object?)"/> — whose
/// <c>CanParse</c> accepts the attachment's content type, and streams its sections with
/// attachment-scoped metadata. When no parser matches, a warning is logged and no sections
/// are produced.
/// </summary>
internal static class EmailAttachmentDispatcher
{
    public static async IAsyncEnumerable<DocumentSection> DispatchAsync(
        IEnumerable<IDocumentParser> parsers,
        IDocumentParser self,
        string fileName,
        string mimeType,
        Stream content,
        DocumentMetadata metadata,
        ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IDocumentParser? parser = null;
        foreach (var p in parsers)
        {
            if (ReferenceEquals(p, self)) continue;
            if (p.CanParse(mimeType))
            {
                parser = p;
                break;
            }
        }

        if (parser is null)
        {
            logger?.LogWarning("No parser registered for attachment content type {ContentType}; skipping {FileName}",
                mimeType, fileName);
            yield break;
        }

        var attachmentMetadata = new DocumentMetadata
        {
            DocumentId = metadata.DocumentId,
            FileName = fileName,
            ContentType = mimeType,
            Tags = metadata.Tags is { Count: > 0 }
                ? new Dictionary<string, string>(metadata.Tags, StringComparer.Ordinal)
                : metadata.Tags,
            CreatedAt = metadata.CreatedAt,
        };

        await foreach (var section in parser.ParseAsync(content, attachmentMetadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section;
        }
    }
}
