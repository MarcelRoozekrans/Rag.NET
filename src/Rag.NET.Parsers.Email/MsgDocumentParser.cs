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
    ILogger<MsgDocumentParser>? logger = null,
    EmailParserOptions? options = null) : IDocumentParser
{
    internal const string MsgContentType = "application/vnd.ms-outlook";

    private readonly EmailParserOptions options = options ?? new EmailParserOptions();

    public bool CanParse(string contentType) =>
        contentType.Equals(MsgContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = new Storage.Message(stream, FileAccess.Read, leaveStreamOpen: true);
        var context = EmbeddedMessageContext.Create(metadata, options);
        int sectionIndex = 0;

        // SectionIndex is stamped exactly once, here: everything below — including any
        // nested message parsed in-process — yields unstamped sections.
        await foreach (var section in ParseMessageAsync(message, context, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseMessageAsync(
        Storage.Message message,
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

    /// <summary>
    /// Parses a nested message in place, or yields nothing after a warning when either
    /// embedded-message limit is reached. Never throws: the parser degrades rather than breaks.
    /// </summary>
    private async IAsyncEnumerable<DocumentSection> ParseEmbeddedAsync(
        Storage.Message embedded,
        EmbeddedMessageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var name = !string.IsNullOrWhiteSpace(embedded.Subject)
            ? embedded.Subject
            : embedded.FileName ?? "(no subject)";

        if (!context.TryEnterEmbedded(name, logger))
            yield break;

        var metadata = EmbeddedMessageMetadata.Create(context.Metadata, name, ".msg", MsgContentType);
        await foreach (var section in ParseMessageAsync(embedded, context.Descend(metadata), cancellationToken).ConfigureAwait(false))
        {
            yield return section;
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
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DocumentSection
            {
                Text = message.BodyText.Trim(),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0, // stamped by ParseAsync
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
        EmbeddedMessageContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Storage.Message.Attachments is a List<object> mixing file attachments
        // (Storage.Attachment) and embedded messages (Storage.Message).
#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan cannot cross yield/await boundaries in async iterators
        foreach (var item in message.Attachments)
#pragma warning restore HLQ012
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Embedded/forwarded emails surface as a nested Storage.Message: a live object,
            // not a stream, so it is parsed in place rather than re-entering the stream-based
            // ParseAsync. It is deliberately not disposed here — it belongs to the outer
            // message's Attachments collection, and disposing an item while enumerating that
            // collection would be the parser destroying data its caller may still read.
            // (Probed against MsgReader 6.1.0: a nested child's BodyText is still readable
            // after the outer message's Dispose(), so the parent does not tear children down.
            // Ownership is by convention here, not enforced by the library.)
            if (item is Storage.Message embedded)
            {
                await foreach (var section in ParseEmbeddedAsync(embedded, context, cancellationToken).ConfigureAwait(false))
                {
                    yield return section;
                }

                continue;
            }

            if (item is not Storage.Attachment attachment)
                continue;

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
                parsers, attachment.FileName, mimeType, attachmentStream, context, logger, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }
}
