namespace Rag.NET;

/// <summary>
/// The four things the shared container machinery needs to report, supplied by the parser package
/// that owns the parse so each keeps its own wording.
/// </summary>
/// <remarks>
/// <para>
/// <b>Injected rather than moved, because the wording is asserted behaviour.</b> Before Phase 3.10
/// these messages lived in <c>EmailParserLog</c> and said things like "Skipping embedded message
/// '{Name}': maximum of {N} embedded messages per document reached", which
/// <c>EmbeddedMessageRecursionTests</c> checks. Moving that text into shared code would have made an
/// archive parser warn about "embedded messages" when it skipped a zip entry; rewording it to suit
/// both would have broken the email tests. So the machinery calls an interface and each package
/// phrases its own warnings — the email package's text is unchanged to the character.
/// </para>
/// <para>
/// Implementations are expected to hold a <c>[LoggerMessage]</c> source-generated logger, which is
/// why nothing here takes an <c>ILogger</c>: the implementation already has one, and a null logger
/// is handled by passing a null <see cref="IContainerLog"/> rather than by checking at every call.
/// </para>
/// </remarks>
public interface IContainerLog
{
    /// <summary>No registered parser accepted an entry's content type, so it was skipped.</summary>
    void NoParserForEntry(string contentType, string entryName);

    /// <summary>
    /// A parser matched an entry and then threw. The entry is skipped and the rest of the container
    /// continues.
    /// </summary>
    /// <remarks>
    /// Takes the <see cref="Exception"/> rather than a message because EPC12/EPC13 make a
    /// <c>catch</c> that reads only <c>ex.Message</c> a build error in this repository, and the
    /// stack trace is the only thing that locates the failure inside the other parser.
    /// </remarks>
    void EntryParserFailed(string parserTypeName, string entryName, Exception exception);

    /// <summary>A nested container was skipped because it would exceed the depth bound.</summary>
    void NestingDepthExceeded(string entryName, int maxNestingDepth);

    /// <summary>A nested container was skipped because the document's entry budget is spent.</summary>
    void EntryBudgetExhausted(string entryName, int maxEntries);
}
