using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Shared attachment dispatch for the email parsers: selects the first registered parser whose
/// <c>CanParse</c> accepts the attachment's content type and streams its sections with
/// attachment-scoped metadata. When no parser matches, a warning is logged and no sections are
/// produced.
/// </summary>
/// <remarks>
/// <para>
/// A parser that matches and then <i>throws</i> costs only its own attachment: the failure is
/// logged with the attachment and the parser type and the next attachment is dispatched. Before
/// Phase 3.11 the exception escaped the whole document parse, so one unreadable attachment lost
/// the body, the headers and every sibling with it. Top-level ingestion deliberately keeps
/// throwing — <c>ParseBehavior</c> and <c>ParentDocumentIngestionBehavior</c> are unchanged. An
/// attachment is sub-content the caller never named; a document the caller passed directly should
/// fail loudly rather than silently index nothing.
/// </para>
/// <para>
/// A message-typed attachment (<c>message/rfc822</c>, <c>application/vnd.ms-outlook</c>) is
/// dispatched only while <see cref="EmbeddedMessageContext"/> still allows it, and carries the
/// reserved depth/budget tags so the child parser continues the same count. This replaced an
/// earlier <c>ReferenceEquals(parser, self)</c> skip, which stopped a parser from re-entering
/// itself but did nothing about an <c>.eml</c> → <c>.msg</c> → <c>.eml</c> chain: consecutive
/// levels there are handled by two <i>different</i> parser instances, so the skip never fired
/// and the chain had no bound at all. Non-message attachments are dispatched exactly as before
/// and never see the reserved tags.
/// </para>
/// </remarks>
internal static class EmailAttachmentDispatcher
{
    public static async IAsyncEnumerable<DocumentSection> DispatchAsync(
        IEnumerable<IDocumentParser> parsers,
        string fileName,
        string mimeType,
        Stream content,
        EmbeddedMessageContext context,
        ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IDocumentParser? parser = null;
        foreach (var p in parsers)
        {
            if (p.CanParse(mimeType))
            {
                parser = p;
                break;
            }
        }

        if (parser is null)
        {
            if (logger is not null)
                EmailParserLog.NoParserForAttachment(logger, mimeType, fileName);

            yield break;
        }

        bool isEmbeddedMessage = IsMessageContentType(mimeType);
        if (isEmbeddedMessage && !context.TryEnterEmbedded(fileName, logger))
            yield break;

        var metadata = context.Metadata;
        var tags = new Dictionary<string, string>(metadata.Tags, StringComparer.Ordinal);
        if (isEmbeddedMessage)
            context.StampChildTags(tags);

        var attachmentMetadata = new DocumentMetadata
        {
            DocumentId = metadata.DocumentId,
            FileName = fileName,
            ContentType = mimeType,
            Tags = tags,
            CreatedAt = metadata.CreatedAt,
        };

        var enumerator = TryCreateEnumerator(parser, content, attachmentMetadata, fileName, logger, cancellationToken);
        if (enumerator is not null)
        {
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    var section = await MoveNextOrContainAsync(enumerator, parser, fileName, logger)
                        .ConfigureAwait(false);
                    if (section is null)
                        break;

                    yield return section;
                }
            }
        }

        if (isEmbeddedMessage)
            context.AdoptChildBudget(tags);
    }

    /// <summary>
    /// Starts the attachment parser's enumeration, containing a parser that throws before it
    /// produces an enumerator at all — a <c>ParseAsync</c> written as an ordinary method rather
    /// than as an iterator throws here rather than on the first <c>MoveNextAsync</c>.
    /// </summary>
    private static IAsyncEnumerator<DocumentSection>? TryCreateEnumerator(
        IDocumentParser parser,
        Stream content,
        DocumentMetadata attachmentMetadata,
        string fileName,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return parser.ParseAsync(content, attachmentMetadata, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Contain(parser, fileName, logger, ex);
            return null;
        }
    }

    /// <summary>
    /// Advances the attachment parser one step, returning the section it produced or
    /// <see langword="null"/> once it is exhausted or has thrown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# forbids <c>yield return</c> inside a <c>try</c> that has a <c>catch</c>, so the
    /// enumeration cannot be written as an <c>await foreach</c> wrapped in containment. It is
    /// driven manually instead: this method holds the <c>try</c>/<c>catch</c> and the iterator
    /// yields outside it.
    /// </para>
    /// <para>
    /// Because containment is per-step rather than per-attachment, a parser that throws
    /// <i>after</i> yielding sections keeps the sections it already yielded — the caller has
    /// consumed them by then, and discarding them would lose content the parser did produce.
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> is rethrown. Cancellation is the caller's
    /// decision about the whole parse, not a failure of this attachment, and swallowing it would
    /// turn a cancelled ingestion into a silently partial one.
    /// </para>
    /// </remarks>
    private static async ValueTask<DocumentSection?> MoveNextOrContainAsync(
        IAsyncEnumerator<DocumentSection> enumerator,
        IDocumentParser parser,
        string fileName,
        ILogger? logger)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false) ? enumerator.Current : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Contain(parser, fileName, logger, ex);
            return null;
        }
    }

    private static void Contain(IDocumentParser parser, string fileName, ILogger? logger, Exception ex)
    {
        if (logger is null)
            return;

        var parserType = parser.GetType();
        EmailParserLog.AttachmentParserFailed(logger, parserType.FullName ?? parserType.Name, fileName, ex);
    }

    /// <summary>
    /// Reports whether an attachment is itself a message, and therefore counts against the
    /// embedded-message limits. The test is on the content type rather than on the resolved
    /// parser's type, so a third-party replacement for either parser is bounded too.
    /// </summary>
    internal static bool IsMessageContentType(string mimeType) =>
        mimeType.Equals(EmailDocumentParser.EmlContentType, StringComparison.OrdinalIgnoreCase) ||
        mimeType.Equals(MsgDocumentParser.MsgContentType, StringComparison.OrdinalIgnoreCase);
}
