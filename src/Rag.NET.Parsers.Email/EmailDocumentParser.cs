using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

public sealed class EmailDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    HtmlDocumentParser htmlParser,
    ILogger<EmailDocumentParser>? logger = null) : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
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

    private async IAsyncEnumerable<DocumentSection> ParseAttachmentsAsync(
        MimeMessage message,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var attachment in message.Attachments.OfType<MimePart>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.Content is null)
                continue;

            var mimeType = $"{attachment.ContentType.MediaType}/{attachment.ContentType.MediaSubtype}";

            using var attachmentStream = new MemoryStream();
            await attachment.Content.DecodeToAsync(attachmentStream, cancellationToken).ConfigureAwait(false);
            attachmentStream.Position = 0;

            await foreach (var section in EmailAttachmentDispatcher.DispatchAsync(
                parsers, this, attachment.FileName, mimeType, attachmentStream, metadata, logger, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseBodyAsync(
        MimeMessage message,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Prefer plain text
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            yield return new DocumentSection
            {
                Text = message.TextBody.Trim(),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0, // re-stamped by caller
            };
            yield break;
        }

        // Fall back to HTML body
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            using var htmlStream = new MemoryStream(Encoding.UTF8.GetBytes(message.HtmlBody));
            await foreach (var section in htmlParser.ParseAsync(htmlStream, metadata, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }
}
