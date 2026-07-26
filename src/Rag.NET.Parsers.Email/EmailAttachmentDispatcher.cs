using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Shared attachment dispatch for the email parsers: selects the first registered parser whose
/// <c>CanParse</c> accepts the attachment's content type and streams its sections with
/// attachment-scoped metadata. When no parser matches, a warning is logged and no sections are
/// produced.
/// </summary>
/// <remarks>
/// A message-typed attachment (<c>message/rfc822</c>, <c>application/vnd.ms-outlook</c>) is
/// dispatched only while <see cref="EmbeddedMessageContext"/> still allows it, and carries the
/// reserved depth/budget tags so the child parser continues the same count. This replaced an
/// earlier <c>ReferenceEquals(parser, self)</c> skip, which stopped a parser from re-entering
/// itself but did nothing about an <c>.eml</c> → <c>.msg</c> → <c>.eml</c> chain: consecutive
/// levels there are handled by two <i>different</i> parser instances, so the skip never fired
/// and the chain had no bound at all. Non-message attachments are dispatched exactly as before
/// and never see the reserved tags.
/// </remarks>
internal static class EmailAttachmentDispatcher
{
    public static async IAsyncEnumerable<DocumentSection> DispatchAsync(
        IEnumerable<IDocumentParser> parsers,
        string fileName,
        string mimeType,
        Stream content,
        EmbeddedMessageContext context,
        ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IDocumentParser? parser = null;
        foreach (var p in parsers)
        {
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

        bool isEmbeddedMessage = IsMessageContentType(mimeType);
        if (isEmbeddedMessage && !context.TryEnterEmbedded(fileName, logger))
            yield break;

        var metadata = context.Metadata;
        var tags = new Dictionary<string, string>(metadata.Tags, StringComparer.Ordinal);
        if (isEmbeddedMessage)
            context.StampChildTags(tags);

        var attachmentMetadata = new DocumentMetadata
        {
            DocumentId = metadata.DocumentId,
            FileName = fileName,
            ContentType = mimeType,
            Tags = tags,
            CreatedAt = metadata.CreatedAt,
        };

        await foreach (var section in parser.ParseAsync(content, attachmentMetadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section;
        }

        if (isEmbeddedMessage)
            context.AdoptChildBudget(tags);
    }

    /// <summary>
    /// Reports whether an attachment is itself a message, and therefore counts against the
    /// embedded-message limits. The test is on the content type rather than on the resolved
    /// parser's type, so a third-party replacement for either parser is bounded too.
    /// </summary>
    internal static bool IsMessageContentType(string mimeType) =>
        mimeType.Equals(EmailDocumentParser.EmlContentType, StringComparison.OrdinalIgnoreCase) ||
        mimeType.Equals(MsgDocumentParser.MsgContentType, StringComparison.OrdinalIgnoreCase);
}
