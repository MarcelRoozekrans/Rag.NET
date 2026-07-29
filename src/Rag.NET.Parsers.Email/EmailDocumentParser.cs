using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Parses <c>.eml</c> files (<c>message/rfc822</c>) via MimeKit: subject becomes a level-1
/// heading section, the body prefers plain text and falls back to HTML through
/// <see cref="HtmlDocumentParser"/>, and attachments are dispatched to the registered parsers
/// via <see cref="EmailAttachmentDispatcher"/>.
/// </summary>
/// <remarks>
/// Embedded messages are walked by <see cref="EmbeddedTraversal"/> over an explicit stack, so
/// nesting depth costs heap rather than CLR stack. This parser holds no method that calls itself.
/// </remarks>
public sealed class EmailDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    HtmlDocumentParser htmlParser,
    ILogger<EmailDocumentParser>? logger = null,
    EmailParserOptions? options = null) : IDocumentParser
{
    internal const string EmlContentType = "message/rfc822";

    private readonly EmailParserOptions options = options ?? new EmailParserOptions();
    private readonly MimeMessageAdapter adapter = new(htmlParser);
    private readonly EmbeddedMessageDescentPolicy policy = new(".eml", EmlContentType, logger);

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

        // SectionIndex is stamped exactly once, here: the traversal — including any embedded
        // message walked in-process — yields unstamped sections.
        await foreach (var section in EmbeddedTraversal.RunAsync(
            message, adapter, context, policy, parsers, logger, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }
    }
}
