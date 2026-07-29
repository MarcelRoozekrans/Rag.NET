using System.Runtime.CompilerServices;
using System.Text;
using MimeKit;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Presents a <see cref="MimeMessage"/> to <see cref="EmbeddedTraversal"/>: subject, body and
/// children, with every MimeKit-specific rule the flattened <c>EmailDocumentParser</c> used to
/// carry inline.
/// </summary>
/// <param name="htmlParser">Sub-parser for the HTML body fallback.</param>
internal sealed class MimeMessageAdapter(HtmlDocumentParser htmlParser) : IMessageAdapter<MimeMessage>
{
    public string? GetSubject(MimeMessage message) => message.Subject;

    public IAsyncEnumerable<DocumentSection> ReadBodyAsync(
        MimeMessage message,
        DocumentMetadata metadata,
        CancellationToken cancellationToken) =>
        ParseBodyAsync(message, metadata, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// An iterator method, so an attachment is inspected only when the driver asks for it and no
    /// attachment content is decoded before its own turn.
    /// </remarks>
    public IEnumerator<MessageChild<MimeMessage>> ReadChildren(MimeMessage message) =>
        Enumerate(message).GetEnumerator();

    private static IEnumerable<MessageChild<MimeMessage>> Enumerate(MimeMessage message)
    {
        foreach (var entity in message.Attachments)
        {
            // Embedded/forwarded emails surface as MessagePart: a live MimeMessage owned by
            // this message's object graph, so the driver walks it in place rather than
            // re-entering the stream-based ParseAsync.
            if (entity is MessagePart embedded)
            {
                if (embedded.Message is { } nested)
                    yield return EmbeddedChild(embedded, nested);

                continue;
            }

            if (entity is not MimePart attachment)
                continue;

            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.Content is not { } content)
                continue;

            yield return new MessageChild<MimeMessage>
            {
                Name = attachment.FileName,
                MimeType = $"{attachment.ContentType.MediaType}/{attachment.ContentType.MediaSubtype}",
                OpenAsync = cancellationToken => DecodeAsync(content, cancellationToken),
            };
        }
    }

    private static MessageChild<MimeMessage> EmbeddedChild(MessagePart embedded, MimeMessage nested) => new()
    {
        Name = !string.IsNullOrWhiteSpace(nested.Subject)
            ? nested.Subject
            : embedded.ContentDisposition?.FileName ?? "(no subject)",
        EmbeddedMessage = nested,
    };

    private static async ValueTask<Stream> DecodeAsync(IMimeContent content, CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await content.DecodeToAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return stream;
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
