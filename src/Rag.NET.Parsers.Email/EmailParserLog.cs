using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Email;

internal static partial class EmailParserLog
{
    [LoggerMessage(EventId = 1045321774, EventName = "embedded_message_depth_limit", Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': maximum embedded depth of {MaxEmbeddedDepth} reached")]
    internal static partial void EmbeddedMessageDepthLimit(ILogger logger, string name, int maxEmbeddedDepth);

    [LoggerMessage(EventId = 1123285508, EventName = "embedded_message_count_limit", Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': maximum of {MaxEmbeddedMessages} embedded messages per document reached")]
    internal static partial void EmbeddedMessageCountLimit(ILogger logger, string name, int maxEmbeddedMessages);

    [LoggerMessage(EventId = 1463375003, EventName = "no_parser_for_attachment", Level = LogLevel.Warning, Message = "No parser registered for attachment content type {ContentType}; skipping {FileName}")]
    internal static partial void NoParserForAttachment(ILogger logger, string contentType, string fileName);

    /// <summary>
    /// The containment warning from <see cref="ContainerEntryDispatcher"/>. It names the parser
    /// as well as the attachment because the attachment alone does not say which registration is
    /// at fault, and the failing parser may be a third-party one the dispatcher only knows through
    /// <c>IDocumentParser</c>. The exception is logged as an exception rather than as its message:
    /// EPC12/EPC13 make a <c>catch</c> that reads only <c>ex.Message</c> a build error, and the
    /// stack trace is the only thing that locates the failure inside the other parser.
    /// </summary>
    [LoggerMessage(EventId = 11322950, EventName = "attachment_parser_failed", Level = LogLevel.Warning, Message = "Parser {ParserType} failed on attachment '{FileName}'; skipping that attachment and continuing")]
    internal static partial void AttachmentParserFailed(ILogger logger, string parserType, string fileName, Exception exception);
}
