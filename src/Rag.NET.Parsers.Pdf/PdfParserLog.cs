using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Pdf;

internal static partial class PdfParserLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Table extraction failed on page {PageNumber}; page parsed as plain text")]
    internal static partial void TableExtractionFailed(ILogger logger, int pageNumber, Exception exception);
}
