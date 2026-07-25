using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Email;

internal static partial class EmailParserLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping embedded message '{Name}': embedded messages are not yet recursed")]
    internal static partial void EmbeddedMessageSkipped(ILogger logger, string name);
}
