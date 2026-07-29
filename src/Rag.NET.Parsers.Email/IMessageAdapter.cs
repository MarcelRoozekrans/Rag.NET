using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Everything <see cref="EmbeddedTraversal"/> needs to walk one message library's object graph.
/// </summary>
/// <remarks>
/// This is the seam that keeps the traversal written once. MimeKit needs
/// <c>MimeContent.DecodeToAsync</c> into a <see cref="MemoryStream"/> to produce an attachment
/// stream; MsgReader hands over a <c>byte[]</c> and needs the <see cref="MimeTypeMap"/> fallback
/// for senders that omit <c>PidTagAttachMimeTag</c>. Those differences live in the adapters; the
/// depth-first walk, the ordering guarantee and the disposal contract live in the driver.
/// </remarks>
/// <typeparam name="TMessage">The library's message type.</typeparam>
internal interface IMessageAdapter<TMessage>
    where TMessage : class
{
    /// <summary>The message's subject, or <see langword="null"/> when it has none.</summary>
    string? GetSubject(TMessage message);

    /// <summary>
    /// The message's body sections. Awaited inline by the driver rather than pushed onto its
    /// stack, so an implementation may nest a bounded sub-parse (for example HTML) here.
    /// </summary>
    IAsyncEnumerable<DocumentSection> ReadBodyAsync(
        TMessage message,
        DocumentMetadata metadata,
        CancellationToken cancellationToken);

    /// <summary>Children in document order. Disposed by the driver when its frame is popped.</summary>
    /// <remarks>
    /// The driver disposes the <i>enumerator</i> and never a message. An implementation whose
    /// nested messages are owned by their parent's collection must not dispose them here.
    /// </remarks>
    IEnumerator<MessageChild<TMessage>> ReadChildren(TMessage message);
}
