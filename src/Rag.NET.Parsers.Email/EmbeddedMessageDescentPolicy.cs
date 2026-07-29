using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// The production <see cref="IDescentPolicy"/>: descends only while
/// <see cref="EmbeddedMessageContext.TryEnterEmbedded"/> still allows it, and names the child
/// through <see cref="EmbeddedMessageMetadata"/>.
/// </summary>
/// <remarks>
/// Depth accounting, budget accounting and the warning behaviour are all
/// <see cref="EmbeddedMessageContext.TryEnterEmbedded"/>'s, exactly as they were when the parsers
/// recursed. Flattening the traversal moved the <i>call site</i> here; it did not move the rules.
/// Both parsers share this type — <c>".eml"</c> plus <see cref="EmailDocumentParser.EmlContentType"/>
/// for one, <c>".msg"</c> plus <see cref="MsgDocumentParser.MsgContentType"/> for the other.
/// </remarks>
/// <param name="extension">Extension appended to the composed child file name.</param>
/// <param name="contentType">Content type the child is parsed under.</param>
/// <param name="logger">Sink for the limit warnings; <see langword="null"/> silences them.</param>
internal sealed class EmbeddedMessageDescentPolicy(string extension, string contentType, ILogger? logger)
    : IDescentPolicy
{
    public EmbeddedMessageContext? TryDescend(EmbeddedMessageContext parent, string name)
    {
        if (!parent.TryEnterEmbedded(name, logger))
            return null;

        return parent.Descend(EmbeddedMessageMetadata.Create(parent.Metadata, name, extension, contentType));
    }
}
