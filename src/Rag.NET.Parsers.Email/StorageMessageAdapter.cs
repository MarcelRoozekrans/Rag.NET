using System.Runtime.CompilerServices;
using System.Text;
using MsgReader.Outlook;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Presents a <see cref="Storage.Message"/> to <see cref="EmbeddedTraversal"/>: subject, body and
/// children, with every MsgReader-specific rule the flattened <c>MsgDocumentParser</c> used to
/// carry inline.
/// </summary>
/// <param name="htmlParser">Sub-parser for the HTML body fallback.</param>
internal sealed class StorageMessageAdapter(HtmlDocumentParser htmlParser) : IMessageAdapter<Storage.Message>
{
    public string? GetSubject(Storage.Message message) => message.Subject;

    public IAsyncEnumerable<DocumentSection> ReadBodyAsync(
        Storage.Message message,
        DocumentMetadata metadata,
        CancellationToken cancellationToken) =>
        ParseBodyAsync(message, metadata, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// An iterator method, so an attachment is inspected only when the driver asks for it. What
    /// the driver disposes when it pops a frame is this compiler-generated enumerator, which owns
    /// nothing: it never reaches a <see cref="Storage.Message"/> — see <see cref="EmbeddedChild"/>.
    /// </remarks>
    public IEnumerator<MessageChild<Storage.Message>> ReadChildren(Storage.Message message) =>
        Enumerate(message).GetEnumerator();

    private static IEnumerable<MessageChild<Storage.Message>> Enumerate(Storage.Message message)
    {
        // Storage.Message.Attachments is a List<object> mixing file attachments
        // (Storage.Attachment) and embedded messages (Storage.Message).
#pragma warning disable HLQ012 // CollectionsMarshal.AsSpan: a Span<T> cannot live across the yield returns below
        foreach (var item in message.Attachments)
#pragma warning restore HLQ012
        {
            if (item is Storage.Message embedded)
            {
                yield return EmbeddedChild(embedded);
                continue;
            }

            if (item is not Storage.Attachment attachment)
                continue;

            if (string.IsNullOrWhiteSpace(attachment.FileName) || attachment.Data is null)
                continue;

            yield return FileChild(attachment);
        }
    }

    /// <summary>
    /// Wraps a nested message for the driver to walk in place.
    /// </summary>
    /// <remarks>
    /// Embedded/forwarded emails surface as a nested <see cref="Storage.Message"/>: a live object,
    /// not a stream, so it is walked in place rather than re-entering the stream-based
    /// <c>ParseAsync</c>. It is deliberately <b>not</b> disposed — it belongs to the outer
    /// message's <c>Attachments</c> collection, and disposing an item while enumerating that
    /// collection would be the parser destroying data its caller may still read. (Probed against
    /// MsgReader 6.1.0: a nested child's <c>BodyText</c> is still readable after the outer
    /// message's <c>Dispose()</c>, so the parent does not tear children down. Ownership is by
    /// convention here, not enforced by the library.)
    /// </remarks>
    private static MessageChild<Storage.Message> EmbeddedChild(Storage.Message embedded) => new()
    {
        Name = !string.IsNullOrWhiteSpace(embedded.Subject)
            ? embedded.Subject
            : embedded.FileName ?? "(no subject)",
        EmbeddedMessage = embedded,
    };

    private static MessageChild<Storage.Message> FileChild(Storage.Attachment attachment)
    {
        // MsgReader's MimeType is populated from the attachment's PidTagAttachMimeTag
        // property, which senders frequently omit — fall back to inferring from the
        // file extension when it is absent.
        var mimeType = !string.IsNullOrWhiteSpace(attachment.MimeType)
            ? attachment.MimeType
            : ContentTypeMap.FromFileName(attachment.FileName);

        var data = attachment.Data;
        return new MessageChild<Storage.Message>
        {
            Name = attachment.FileName,
            MimeType = mimeType,
            OpenAsync = _ => new ValueTask<Stream>(new MemoryStream(data)),
        };
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
}
