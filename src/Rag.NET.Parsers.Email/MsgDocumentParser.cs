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
public sealed class MsgDocumentParser : IDocumentParser
{
    internal const string MsgContentType = "application/vnd.ms-outlook";

    private readonly IEnumerable<IDocumentParser> parsers;
    private readonly EmailParserOptions options;
    private readonly StorageMessageAdapter adapter;
    private readonly EmailContainerLog? containerLog;
    private readonly EmbeddedMessageDescentPolicy policy;

    /// <summary>Creates the parser.</summary>
    /// <param name="parsers">The registered parsers attachments are dispatched to.</param>
    /// <param name="htmlParser">Used for an HTML body when there is no plain-text one.</param>
    /// <param name="logger">Sink for the warnings; <see langword="null"/> silences them.</param>
    /// <param name="options">The nesting bounds; defaults are used when omitted.</param>
    /// <remarks>See <see cref="EmailDocumentParser"/>'s constructor for why this is written out.</remarks>
    public MsgDocumentParser(
        IEnumerable<IDocumentParser> parsers,
        HtmlDocumentParser htmlParser,
        ILogger<MsgDocumentParser>? logger = null,
        EmailParserOptions? options = null)
    {
        this.parsers = parsers;
        this.options = options ?? new EmailParserOptions();
        adapter = new StorageMessageAdapter(htmlParser);
        containerLog = EmailContainerLog.For(logger);
        policy = new EmbeddedMessageDescentPolicy(".msg", MsgContentType, containerLog);
    }

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
