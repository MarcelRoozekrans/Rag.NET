using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Pdf;

internal static partial class PdfParserLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Table extraction failed on page {PageNumber}; page parsed as plain text")]
    internal static partial void TableExtractionFailed(ILogger logger, int pageNumber, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OCR fallback found no embedded images on page {PageNumber}; page parsed as plain text")]
    internal static partial void OcrNoImages(ILogger logger, int pageNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OCR produced no text on page {PageNumber}; page parsed as plain text")]
    internal static partial void OcrNoText(ILogger logger, int pageNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OCR fallback failed on page {PageNumber}; page parsed as plain text")]
    internal static partial void OcrFailed(ILogger logger, int pageNumber, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document-level OCR skipped: the document has {PageCount} pages, above the MaxOcrPages cap of {MaxOcrPages}; pages parsed as plain text")]
    internal static partial void DocumentOcrPageCapExceeded(ILogger logger, int pageCount, int maxOcrPages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document-level OCR failed; document parsed as plain text")]
    internal static partial void DocumentOcrFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document-level OCR returned no text for page {PageNumber}; page parsed as plain text")]
    internal static partial void DocumentOcrNoText(ILogger logger, int pageNumber);
}
