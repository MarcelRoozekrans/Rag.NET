using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET;

/// <summary>
/// Shared entry dispatch for container parsers: selects the first registered parser whose
/// <c>CanParse</c> accepts an entry's content type and streams its sections with entry-scoped
/// metadata. When no parser matches, a warning is logged and no sections are produced.
/// </summary>
/// <remarks>
/// <para>
/// A parser that matches and then <i>throws</i> costs only its own entry: the failure is logged with
/// the entry and the parser type and the next entry is dispatched. Before Phase 3.11 the exception
/// escaped the whole document parse, so one unreadable attachment lost the body, the headers and
/// every sibling with it. Top-level ingestion deliberately keeps throwing — <c>ParseBehavior</c> and
/// <c>ParentDocumentIngestionBehavior</c> are unchanged. An entry is sub-content the caller never
/// named; a document the caller passed directly should fail loudly rather than silently index
/// nothing.
/// </para>
/// <para>
/// An entry that is itself a container is dispatched only while <see cref="ContainerContext"/> still
/// allows it, and carries the reserved depth/budget tags so the child parser continues the same
/// count. This replaced an earlier <c>ReferenceEquals(parser, self)</c> skip, which stopped a parser
/// from re-entering itself but did nothing about an <c>.eml</c> → <c>.msg</c> → <c>.eml</c> chain:
/// consecutive levels there are handled by two <i>different</i> parser instances, so the skip never
/// fired and the chain had no bound at all. Non-container entries are dispatched exactly as before
/// and never see the reserved tags.
/// </para>
/// <para>
/// Whether an entry counts as a container is decided by <see cref="ContainerContentTypes"/> rather
/// than by the calling package, because the budget is one total across the document — see that type
/// for why a per-package test leaves an alternating chain unbounded.
/// </para>
/// </remarks>
public static class ContainerEntryDispatcher
{
    /// <summary>
    /// Dispatches one container entry to whichever registered parser accepts
    /// <paramref name="contentType"/>, streaming the sections it produces. Yields no sections,
    /// logging a warning instead, when no parser matches or the entry is itself a nested container
    /// that <paramref name="context"/> refuses (depth or budget exhausted). A matched parser that
    /// throws is contained per this entry — the failure is logged and dispatch moves on rather
    /// than propagating out of the whole document parse.
    /// </summary>
    public static async IAsyncEnumerable<DocumentSection> DispatchAsync(
        IEnumerable<IDocumentParser> parsers,
        string entryName,
        string contentType,
        Stream content,
        ContainerContext context,
        IContainerLog? log,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IDocumentParser? parser = null;
        foreach (var p in parsers)
        {
            if (p.CanParse(contentType))
            {
                parser = p;
                break;
            }
        }

        if (parser is null)
        {
            log?.NoParserForEntry(contentType, entryName);
            yield break;
        }

        bool isNestedContainer = ContainerContentTypes.IsNestedContainer(contentType);
        if (isNestedContainer && !context.TryEnterNested(entryName, log))
            yield break;

        var metadata = context.Metadata;
        var tags = new Dictionary<string, MetadataValue>(metadata.Tags, StringComparer.Ordinal);
        if (isNestedContainer)
            context.StampChildTags(tags);

        var entryMetadata = new DocumentMetadata
        {
            DocumentId = metadata.DocumentId,
            FileName = entryName,
            ContentType = contentType,
            Tags = tags,
            CreatedAt = metadata.CreatedAt,
        };

        var enumerator = TryCreateEnumerator(parser, content, entryMetadata, entryName, log, cancellationToken);
        if (enumerator is not null)
        {
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    var section = await MoveNextOrContainAsync(enumerator, parser, entryName, log)
                        .ConfigureAwait(false);
                    if (section is null)
                        break;

                    yield return section;
                }
            }
        }

        if (isNestedContainer)
            context.AdoptChildBudget(tags);
    }

    /// <summary>
    /// Starts the entry parser's enumeration, containing a parser that throws before it produces an
    /// enumerator at all — a <c>ParseAsync</c> written as an ordinary method rather than as an
    /// iterator throws here rather than on the first <c>MoveNextAsync</c>.
    /// </summary>
    private static IAsyncEnumerator<DocumentSection>? TryCreateEnumerator(
        IDocumentParser parser,
        Stream content,
        DocumentMetadata entryMetadata,
        string entryName,
        IContainerLog? log,
        CancellationToken cancellationToken)
    {
        try
        {
            return parser.ParseAsync(content, entryMetadata, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Contain(parser, entryName, log, ex);
            return null;
        }
    }

    /// <summary>
    /// Advances the entry parser one step, returning the section it produced or
    /// <see langword="null"/> once it is exhausted or has thrown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# forbids <c>yield return</c> inside a <c>try</c> that has a <c>catch</c>, so the enumeration
    /// cannot be written as an <c>await foreach</c> wrapped in containment. It is driven manually
    /// instead: this method holds the <c>try</c>/<c>catch</c> and the iterator yields outside it.
    /// </para>
    /// <para>
    /// Because containment is per-step rather than per-entry, a parser that throws <i>after</i>
    /// yielding sections keeps the sections it already yielded — the caller has consumed them by
    /// then, and discarding them would lose content the parser did produce.
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> is rethrown. Cancellation is the caller's decision
    /// about the whole parse, not a failure of this entry, and swallowing it would turn a cancelled
    /// ingestion into a silently partial one.
    /// </para>
    /// </remarks>
    private static async ValueTask<DocumentSection?> MoveNextOrContainAsync(
        IAsyncEnumerator<DocumentSection> enumerator,
        IDocumentParser parser,
        string entryName,
        IContainerLog? log)
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
            Contain(parser, entryName, log, ex);
            return null;
        }
    }

    private static void Contain(IDocumentParser parser, string entryName, IContainerLog? log, Exception ex)
    {
        if (log is null)
            return;

        var parserType = parser.GetType();
        log.EntryParserFailed(parserType.FullName ?? parserType.Name, entryName, ex);
    }
}
