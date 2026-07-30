using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Archive;

/// <summary>
/// Supplies this package's wording to the shared container machinery, mirroring
/// <c>EmailContainerLog</c>.
/// </summary>
/// <remarks>
/// Holds nothing but the logger, so an instance per parse would be pointless — one per parser is
/// enough and keeps <see cref="ContainerEntryDispatcher"/> free of any <c>ILogger</c> parameter. A
/// null logger is represented by a null <see cref="IContainerLog"/> rather than by a guard at every
/// call site, which is why <see cref="For"/> returns null rather than a no-op instance.
/// </remarks>
internal sealed class ArchiveContainerLog(ILogger logger) : IContainerLog
{
    /// <summary>Returns a log for <paramref name="logger"/>, or null when there is nothing to log to.</summary>
    public static ArchiveContainerLog? For(ILogger? logger) => logger is null ? null : new ArchiveContainerLog(logger);

    public void NoParserForEntry(string contentType, string entryName) =>
        ArchiveParserLog.NoParserForEntry(logger, contentType, entryName);

    public void EntryParserFailed(string parserTypeName, string entryName, Exception exception) =>
        ArchiveParserLog.EntryParserFailed(logger, parserTypeName, entryName, exception);

    public void NestingDepthExceeded(string entryName, int maxNestingDepth) =>
        ArchiveParserLog.NestingDepthExceeded(logger, entryName, maxNestingDepth);

    public void EntryBudgetExhausted(string entryName, int maxEntries) =>
        ArchiveParserLog.NestedContainerCountLimit(logger, entryName, maxEntries);
}
