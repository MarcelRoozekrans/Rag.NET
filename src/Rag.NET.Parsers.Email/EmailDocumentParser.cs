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
    ILogger<EmailDocumentParser>? logger = null,
    EmailParserOptions? options = null) : IDocumentParser
{
    internal const string EmlContentType = "message/rfc822";

    private readonly EmailParserOptions options = options ?? new EmailParserOptions();

    public bool CanParse(string contentType) =>
        contentType.Equals(EmlContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        var context = EmbeddedMessageContext.Create(metadata, options);
        int sectionIndex = 0;

        // SectionIndex is stamped exactly once, here: everything below — including any
        // embedded message parsed in-process — yields unstamped sections.
        await foreach (var section in ParseMessageAsync(message, context, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseMessageAsync(
        MimeMessage message,
        EmbeddedMessageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var metadata = context.Metadata;

        // Subject section
        if (!string.IsNullOrWhiteSpace(message.Subject))
        {
            yield return new DocumentSection
            {
                Text = message.Subject,
                DocumentId = metadata.DocumentId,
                Heading = message.Subject,
                HeadingLevel = 1,
                SectionIndex = 0, // stamped by ParseAsync
            };
        }

        // Body sections
        await foreach (var section in ParseBodyAsync(message, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section;
        }

        // Attachment sections
        await foreach (var section in ParseAttachmentsAsync(message, context, cancellationToken).ConfigureAwait(false))
        {
            yield return section;
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseAttachmentsAsync(
        MimeMessage message,
        EmbeddedMessageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var entity in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Embedded/forwarded emails surface as MessagePart: a live MimeMessage owned by
            // this message's object graph, so it is parsed in place rather than re-entering
            // the stream-based ParseAsync.
            if (entity is MessagePart embedded)
            {
                await foreach (var section in ParseEmbeddedAsync(embedded, context, cancellationToken).ConfigureAwait(false))
                {
                    yield return section;
                }

                continue;
            }

            if (entity is not MimePart attachment)
                continue;

            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.Content is null)
                continue;

            var mimeType = $"{attachment.ContentType.MediaType}/{attachment.ContentType.MediaSubtype}";

            using var attachmentStream = new MemoryStream();
            await attachment.Content.DecodeToAsync(attachmentStream, cancellationToken).ConfigureAwait(false);
            attachmentStream.Position = 0;

            await foreach (var section in EmailAttachmentDispatcher.DispatchAsync(
                parsers, attachment.FileName, mimeType, attachmentStream, context, logger, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }

    /// <summary>
    /// Parses an embedded message in place, or yields nothing after a warning when either
    /// embedded-message limit is reached. Never throws: the parser degrades rather than breaks.
    /// </summary>
    private async IAsyncEnumerable<DocumentSection> ParseEmbeddedAsync(
        MessagePart embedded,
        EmbeddedMessageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nested = embedded.Message;
        if (nested is null)
            yield break;

        var name = !string.IsNullOrWhiteSpace(nested.Subject)
            ? nested.Subject
            : embedded.ContentDisposition?.FileName ?? "(no subject)";

        if (!context.TryEnterEmbedded(name, logger))
            yield break;

        var metadata = EmbeddedMessageMetadata.Create(context.Metadata, name, ".eml", EmlContentType);
        await foreach (var section in ParseMessageAsync(nested, context.Descend(metadata), cancellationToken).ConfigureAwait(false))
        {
            yield return section;
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
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DocumentSection
            {
                Text = message.TextBody.Trim(),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0, // stamped by ParseAsync
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
