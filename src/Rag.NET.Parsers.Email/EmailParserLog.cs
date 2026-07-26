using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Email;

internal static partial class EmailParserLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': maximum embedded depth of {MaxEmbeddedDepth} reached")]
    internal static partial void EmbeddedMessageDepthLimit(ILogger logger, string name, int maxEmbeddedDepth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': maximum of {MaxEmbeddedMessages} embedded messages per document reached")]
    internal static partial void EmbeddedMessageCountLimit(ILogger logger, string name, int maxEmbeddedMessages);
}
