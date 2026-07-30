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
/// via <see cref="ContainerEntryDispatcher"/>.
/// </summary>
/// <remarks>
/// Embedded messages are walked by <see cref="EmbeddedTraversal"/> over an explicit stack, so
/// nesting depth costs heap rather than CLR stack. This parser holds no method that calls itself.
/// </remarks>
public sealed class EmailDocumentParser : IDocumentParser
{
    internal const string EmlContentType = "message/rfc822";

    private readonly IEnumerable<IDocumentParser> parsers;
    private readonly EmailParserOptions options;
    private readonly MimeMessageAdapter adapter;
    private readonly EmailContainerLog? containerLog;
    private readonly EmbeddedMessageDescentPolicy policy;

    /// <summary>Creates the parser.</summary>
    /// <param name="parsers">The registered parsers attachments are dispatched to.</param>
    /// <param name="htmlParser">Used for an HTML body when there is no plain-text one.</param>
    /// <param name="logger">Sink for the warnings; <see langword="null"/> silences them.</param>
    /// <param name="options">The nesting bounds; defaults are used when omitted.</param>
    /// <remarks>
    /// Written out rather than left as a primary constructor because <see cref="policy"/> has to be
    /// handed the <i>same</i> <see cref="EmailContainerLog"/> the traversal logs through, and a field
    /// initializer cannot read another field. Calling <c>EmailContainerLog.For</c> twice built two
    /// adapters over one logger, which worked and said something untrue about the design.
    /// </remarks>
    public EmailDocumentParser(
        IEnumerable<IDocumentParser> parsers,
        HtmlDocumentParser htmlParser,
        ILogger<EmailDocumentParser>? logger = null,
        EmailParserOptions? options = null)
    {
        this.parsers = parsers;
        this.options = options ?? new EmailParserOptions();
        adapter = new MimeMessageAdapter(htmlParser);
        containerLog = EmailContainerLog.For(logger);
        policy = new EmbeddedMessageDescentPolicy(".eml", EmlContentType, containerLog);
    }

    public bool CanParse(string contentType) =>
        contentType.Equals(EmlContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        var context = ContainerContext.Create(metadata, options.ToContainerLimits());
        int sectionIndex = 0;

        // SectionIndex is stamped exactly once, here: the traversal — including any embedded
        // message walked in-process — yields unstamped sections.
        await foreach (var section in EmbeddedTraversal.RunAsync(
            message, adapter, context, policy, parsers, containerLog, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }
    }
}
