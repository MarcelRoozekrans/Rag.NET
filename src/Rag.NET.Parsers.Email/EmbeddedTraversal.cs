using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Walks a message and everything embedded in it depth-first, over an explicit
/// <see cref="Stack{T}"/> of frames rather than the call stack.
/// </summary>
/// <remarks>
/// <para>
/// The traversal this replaced ran <c>ParseMessageAsync → ParseAttachmentsAsync →
/// ParseEmbeddedAsync → ParseMessageAsync</c>, three async iterators per nesting level, so pulling
/// one section from depth <i>N</i> drove <i>3N</i> nested <c>MoveNextAsync</c> calls. Measured on
/// this parser, 480 levels survived and 500 terminated the process with <c>0xC00000FD</c>
/// <c>STATUS_STACK_OVERFLOW</c>, which no <c>catch</c> can intercept. Here the CLR stack depth is
/// constant regardless of nesting; depth costs heap.
/// </para>
/// <para>
/// The structure is a stack drained LIFO, not a work queue: a queue is FIFO, and FIFO would turn
/// this depth-first walk breadth-first, reordering every emitted section.
/// </para>
/// </remarks>
internal static class EmbeddedTraversal
{
    /// <summary>
    /// Streams the sections of <paramref name="root"/> and of every message embedded in it that
    /// <paramref name="policy"/> admits. Sections are yielded unstamped; the caller stamps
    /// <see cref="DocumentSection.SectionIndex"/> exactly once.
    /// </summary>
    public static async IAsyncEnumerable<DocumentSection> RunAsync<TMessage>(
        TMessage root,
        IMessageAdapter<TMessage> adapter,
        EmbeddedMessageContext context,
        IDescentPolicy policy,
        IEnumerable<IDocumentParser> parsers,
        ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var stack = new Stack<Frame<TMessage>>();
        try
        {
            stack.Push(NewFrame(root, context, adapter));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Peek, not pop: the frame stays until its children run out, so a parent's child
                // i expands completely before i+1 is touched. That is the ordering guarantee.
                var frame = stack.Peek();

                if (!frame.HeaderEmitted)
                {
                    frame.HeaderEmitted = true;
                    await foreach (var section in EmitHeaderAsync(frame, adapter, cancellationToken).ConfigureAwait(false))
                    {
                        yield return section;
                    }
                }

                if (!frame.Children.MoveNext())
                {
                    stack.Pop().Children.Dispose();
                    continue;
                }

                var child = frame.Children.Current;
                if (child.EmbeddedMessage is { } embedded)
                {
                    var childContext = policy.TryDescend(frame.Context, child.Name);
                    if (childContext is not null)
                        stack.Push(NewFrame(embedded, childContext, adapter));

                    continue;
                }

                // Dispatcher hops are awaited inline, never pushed: they are bounded, and pushing
                // them would make the stack carry two kinds of frame for no gain.
                await foreach (var section in DispatchAsync(child, frame.Context, parsers, logger, cancellationToken).ConfigureAwait(false))
                {
                    yield return section;
                }
            }
        }
        finally
        {
            // A consumer breaking out of the await foreach, or a cancellation, arrives here
            // through DisposeAsync — so every enumerator still on the stack is released.
            while (stack.Count > 0)
            {
                stack.Pop().Children.Dispose();
            }
        }
    }

    /// <summary>Emits a frame's subject section, then its body sections.</summary>
    private static async IAsyncEnumerable<DocumentSection> EmitHeaderAsync<TMessage>(
        Frame<TMessage> frame,
        IMessageAdapter<TMessage> adapter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TMessage : class
    {
        var metadata = frame.Context.Metadata;
        var subject = adapter.GetSubject(frame.Message);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            yield return new DocumentSection
            {
                Text = subject,
                DocumentId = metadata.DocumentId,
                Heading = subject,
                HeadingLevel = 1,
                SectionIndex = 0, // stamped by the calling ParseAsync
            };
        }

        await foreach (var section in adapter.ReadBodyAsync(frame.Message, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section;
        }
    }

    /// <summary>Opens a file attachment and streams whatever parser claims its content type.</summary>
    private static async IAsyncEnumerable<DocumentSection> DispatchAsync<TMessage>(
        MessageChild<TMessage> child,
        EmbeddedMessageContext context,
        IEnumerable<IDocumentParser> parsers,
        ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TMessage : class
    {
        if (child.OpenAsync is null || child.MimeType is null)
            yield break;

        var content = await child.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (content.ConfigureAwait(false))
        {
            await foreach (var section in EmailAttachmentDispatcher.DispatchAsync(
                parsers, child.Name, child.MimeType, content, context, logger, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }

    private static Frame<TMessage> NewFrame<TMessage>(
        TMessage message,
        EmbeddedMessageContext context,
        IMessageAdapter<TMessage> adapter)
        where TMessage : class =>
        new()
        {
            Message = message,
            Context = context,
            Children = adapter.ReadChildren(message),
        };

    /// <summary>One message plus its position in that message's child list.</summary>
    private sealed class Frame<TMessage>
        where TMessage : class
    {
        public required TMessage Message { get; init; }

        public required EmbeddedMessageContext Context { get; init; }

        public required IEnumerator<MessageChild<TMessage>> Children { get; init; }

        public bool HeaderEmitted { get; set; }
    }
}
