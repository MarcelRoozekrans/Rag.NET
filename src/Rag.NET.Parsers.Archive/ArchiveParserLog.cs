using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Archive;

/// <summary>
/// This package's wording for the four things the shared container machinery reports.
/// </summary>
/// <remarks>
/// Separate from <c>EmailParserLog</c> on purpose: the email package says "embedded message" and
/// "attachment", which would be the wrong nouns for a zip entry, and neutralising its text would
/// break the email tests that assert it. See <see cref="IContainerLog"/> for why the machinery calls
/// an interface rather than owning either wording.
/// </remarks>
internal static partial class ArchiveParserLog
{
    [LoggerMessage(EventId = 1178788924, EventName = "no_parser_for_entry", Level = LogLevel.Warning, Message = "No parser registered for archive entry content type {ContentType}; skipping {FileName}")]
    internal static partial void NoParserForEntry(ILogger logger, string contentType, string fileName);

    /// <summary>
    /// The containment warning from <see cref="ContainerEntryDispatcher"/>. It names the parser as
    /// well as the entry because the entry alone does not say which registration is at fault, and
    /// the failing parser may be a third-party one this package only knows through
    /// <c>IDocumentParser</c>. The exception is logged as an exception rather than as its message:
    /// EPC12/EPC13 make a <c>catch</c> that reads only <c>ex.Message</c> a build error here, and the
    /// stack trace is the only thing that locates the failure inside the other parser.
    /// </summary>
    [LoggerMessage(EventId = 1997169265, EventName = "entry_parser_failed", Level = LogLevel.Warning, Message = "Parser {ParserType} failed on archive entry '{FileName}'; skipping that entry and continuing")]
    internal static partial void EntryParserFailed(ILogger logger, string parserType, string fileName, Exception exception);

    [LoggerMessage(EventId = 1317606987, EventName = "nesting_depth_exceeded", Level = LogLevel.Warning, Message = "Skipping nested container '{Name}': maximum container nesting depth of {MaxNestingDepth} reached")]
    internal static partial void NestingDepthExceeded(ILogger logger, string name, int maxNestingDepth);

    [LoggerMessage(EventId = 871319645, EventName = "nested_container_count_limit", Level = LogLevel.Warning, Message = "Skipping nested container '{Name}': maximum of {MaxNestedContainers} nested containers per document reached")]
    internal static partial void NestedContainerCountLimit(ILogger logger, string name, int maxNestedContainers);
}
