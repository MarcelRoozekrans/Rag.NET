using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Email;

internal static partial class EmailParserLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': maximum embedded depth of {MaxEmbeddedDepth} reached")]
    internal static partial void EmbeddedMessageDepthLimit(ILogger logger, string name, int maxEmbeddedDepth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': maximum of {MaxEmbeddedMessages} embedded messages per document reached")]
    internal static partial void EmbeddedMessageCountLimit(ILogger logger, string name, int maxEmbeddedMessages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No parser registered for attachment content type {ContentType}; skipping {FileName}")]
    internal static partial void NoParserForAttachment(ILogger logger, string contentType, string fileName);

    /// <summary>
    /// The containment warning from <see cref="ContainerEntryDispatcher"/>. It names the parser
    /// as well as the attachment because the attachment alone does not say which registration is
    /// at fault, and the failing parser may be a third-party one the dispatcher only knows through
    /// <c>IDocumentParser</c>. The exception is logged as an exception rather than as its message:
    /// EPC12/EPC13 make a <c>catch</c> that reads only <c>ex.Message</c> a build error, and the
    /// stack trace is the only thing that locates the failure inside the other parser.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Parser {ParserType} failed on attachment '{FileName}'; skipping that attachment and continuing")]
    internal static partial void AttachmentParserFailed(ILogger logger, string parserType, string fileName, Exception exception);
}
