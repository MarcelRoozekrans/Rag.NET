using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MsgReader.Outlook;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Parses Outlook <c>.msg</c> files (<c>application/vnd.ms-outlook</c>) via MsgReader:
/// subject becomes a level-1 heading section, the body prefers plain text and falls back
/// to HTML through <see cref="HtmlDocumentParser"/>, and attachments are dispatched to
/// the registered parsers via <see cref="EmailAttachmentDispatcher"/>.
/// </summary>
public sealed class MsgDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    HtmlDocumentParser htmlParser,
    ILogger<MsgDocumentParser>? logger = null) : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("application/vnd.ms-outlook", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = new Storage.Message(stream, FileAccess.Read, leaveStreamOpen: true);
        int sectionIndex = 0;

        // Subject section
        if (!string.IsNullOrWhiteSpace(message.Subject))
        {
            yield return new DocumentSection
            {
                Text = message.Subject,
                DocumentId = metadata.DocumentId,
                Heading = message.Subject,
                HeadingLevel = 1,
                SectionIndex = sectionIndex++,
            };
        }

        // Body sections
        await foreach (var section in ParseBodyAsync(message, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }

        // Attachment sections
        await foreach (var section in ParseAttachmentsAsync(message, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseBodyAsync(
        Storage.Message message,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Prefer plain text
        if (!string.IsNullOrWhiteSpace(message.BodyText))
        {
            yield return new DocumentSection
            {
                Text = message.BodyText.Trim(),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0, // re-stamped by caller
            };
            yield break;
        }

        // Fall back to HTML body
        if (!string.IsNullOrWhiteSpace(message.BodyHtml))
        {
            using var htmlStream = new MemoryStream(Encoding.UTF8.GetBytes(message.BodyHtml));
            await foreach (var section in htmlParser.ParseAsync(htmlStream, metadata, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseAttachmentsAsync(
        Storage.Message message,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Storage.Message.Attachments is a List<object> mixing file attachments
        // (Storage.Attachment) and embedded messages (Storage.Message); only file
        // attachments are dispatched here.
        foreach (var attachment in message.Attachments.OfType<Storage.Attachment>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.Data is null)
                continue;

            // MsgReader's MimeType is populated from the attachment's PidTagAttachMimeTag
            // property, which senders frequently omit — fall back to inferring from the
            // file extension when it is absent.
            var mimeType = !string.IsNullOrWhiteSpace(attachment.MimeType)
                ? attachment.MimeType
                : MimeTypeMap.FromFileName(attachment.FileName);

            using var attachmentStream = new MemoryStream(attachment.Data);

            await foreach (var section in EmailAttachmentDispatcher.DispatchAsync(
                parsers, this, attachment.FileName, mimeType, attachmentStream, metadata, logger, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }
}
