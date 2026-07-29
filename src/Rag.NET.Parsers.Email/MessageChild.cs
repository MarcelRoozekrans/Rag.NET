namespace Rag.NET.Parsers.Email;

/// <summary>
/// One child of a message: either a nested message the driver descends into, or a file
/// attachment it dispatches. A non-null <see cref="EmbeddedMessage"/> means descend.
/// </summary>
/// <typeparam name="TMessage">The library's message type.</typeparam>
internal sealed class MessageChild<TMessage>
    where TMessage : class
{
    /// <summary>The nested message, or <see langword="null"/> for a file attachment.</summary>
    public TMessage? EmbeddedMessage { get; init; }

    /// <summary>
    /// The child's name: an attachment file name, or an embedded message's subject with the
    /// fallbacks its adapter applies.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The attachment's content type. Unused for an embedded message.</summary>
    public string? MimeType { get; init; }

    /// <summary>
    /// Opens the attachment's content. Deferred rather than a ready <see cref="Stream"/> because
    /// MimeKit needs an async decode to produce one and MsgReader hands over a <c>byte[]</c>:
    /// opening eagerly would decode every attachment in a message before the first is dispatched.
    /// The driver disposes what this returns. <see langword="null"/> for an embedded message.
    /// </summary>
    public Func<CancellationToken, ValueTask<Stream>>? OpenAsync { get; init; }
}
