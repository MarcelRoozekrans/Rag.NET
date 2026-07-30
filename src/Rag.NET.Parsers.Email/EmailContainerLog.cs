using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Supplies the email parsers' wording to the shared container machinery.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the messages did not have to move. Phase 3.10 promoted the container context and
/// the entry dispatcher into <c>Rag.NET.Abstractions</c>, and both used to log through
/// <see cref="EmailParserLog"/> directly — with wording like "Skipping embedded message '{Name}'"
/// that <c>EmbeddedMessageRecursionTests</c> asserts. Sharing that text would have made an archive
/// parser warn about embedded messages when it skipped a zip entry; neutralising it would have
/// broken those tests. The machinery calls <see cref="IContainerLog"/> instead and each package
/// keeps its own phrasing, so <b>every message here is unchanged to the character</b>.
/// </para>
/// <para>
/// One instance per parse is unnecessary — this holds nothing but the logger — but it is cheap and
/// keeps the shared machinery free of any <c>ILogger</c> parameter. A null logger is represented by
/// a null <see cref="IContainerLog"/> rather than by a guard at every call site.
/// </para>
/// </remarks>
internal sealed class EmailContainerLog(ILogger logger) : IContainerLog
{
    /// <summary>Returns a log for <paramref name="logger"/>, or null when there is nothing to log to.</summary>
    public static EmailContainerLog? For(ILogger? logger) => logger is null ? null : new EmailContainerLog(logger);

    public void NoParserForEntry(string contentType, string entryName) =>
        EmailParserLog.NoParserForAttachment(logger, contentType, entryName);

    public void EntryParserFailed(string parserTypeName, string entryName, Exception exception) =>
        EmailParserLog.AttachmentParserFailed(logger, parserTypeName, entryName, exception);

    public void NestingDepthExceeded(string entryName, int maxNestingDepth) =>
        EmailParserLog.EmbeddedMessageDepthLimit(logger, entryName, maxNestingDepth);

    public void EntryBudgetExhausted(string entryName, int maxEntries) =>
        EmailParserLog.EmbeddedMessageCountLimit(logger, entryName, maxEntries);
}
