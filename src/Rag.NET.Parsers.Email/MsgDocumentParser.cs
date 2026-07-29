using System.Runtime.CompilerServices;
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
/// the registered parsers via <see cref="ContainerEntryDispatcher"/>.
/// </summary>
/// <remarks>
/// Nested messages are walked by <see cref="EmbeddedTraversal"/> over an explicit stack, so
/// nesting depth costs heap rather than CLR stack. This parser holds no method that calls itself.
/// </remarks>
public sealed class MsgDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    HtmlDocumentParser htmlParser,
    ILogger<MsgDocumentParser>? logger = null,
    EmailParserOptions? options = null) : IDocumentParser
{
    internal const string MsgContentType = "application/vnd.ms-outlook";

    private readonly EmailParserOptions options = options ?? new EmailParserOptions();
    private readonly StorageMessageAdapter adapter = new(htmlParser);
    private readonly EmailContainerLog? containerLog = EmailContainerLog.For(logger);

    private readonly EmbeddedMessageDescentPolicy policy = new(".msg", MsgContentType, EmailContainerLog.For(logger));

    public bool CanParse(string contentType) =>
        contentType.Equals(MsgContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = new Storage.Message(stream, FileAccess.Read, leaveStreamOpen: true);
        var context = ContainerContext.Create(metadata, options.ToContainerLimits());
        int sectionIndex = 0;

        // SectionIndex is stamped exactly once, here: the traversal — including any nested
        // message walked in-process — yields unstamped sections.
        await foreach (var section in EmbeddedTraversal.RunAsync(
            message, adapter, context, policy, parsers, containerLog, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }
    }
}
