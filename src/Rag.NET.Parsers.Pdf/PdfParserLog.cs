using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Pdf;

internal static partial class PdfParserLog
{
    [LoggerMessage(EventId = 861113829, EventName = "table_extraction_failed", Level = LogLevel.Warning, Message = "Table extraction failed on page {PageNumber}; page parsed as plain text")]
    internal static partial void TableExtractionFailed(ILogger logger, int pageNumber, Exception exception);

    [LoggerMessage(EventId = 1456612726, EventName = "ocr_no_images", Level = LogLevel.Warning, Message = "OCR fallback found no embedded images on page {PageNumber}; page parsed as plain text")]
    internal static partial void OcrNoImages(ILogger logger, int pageNumber);

    [LoggerMessage(EventId = 1229261379, EventName = "ocr_no_text", Level = LogLevel.Warning, Message = "OCR produced no text on page {PageNumber}; page parsed as plain text")]
    internal static partial void OcrNoText(ILogger logger, int pageNumber);

    [LoggerMessage(EventId = 1573797210, EventName = "ocr_failed", Level = LogLevel.Warning, Message = "OCR fallback failed on page {PageNumber}; page parsed as plain text")]
    internal static partial void OcrFailed(ILogger logger, int pageNumber, Exception exception);

    [LoggerMessage(EventId = 1894076316, EventName = "document_ocr_page_cap_exceeded", Level = LogLevel.Warning, Message = "Document-level OCR skipped: the document has {PageCount} pages, above the MaxOcrPages cap of {MaxOcrPages}; pages parsed as plain text")]
    internal static partial void DocumentOcrPageCapExceeded(ILogger logger, int pageCount, int maxOcrPages);

    [LoggerMessage(EventId = 575496079, EventName = "document_ocr_failed", Level = LogLevel.Warning, Message = "Document-level OCR failed; document parsed as plain text")]
    internal static partial void DocumentOcrFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 633467404, EventName = "document_ocr_no_text", Level = LogLevel.Warning, Message = "Document-level OCR returned no text for page {PageNumber}; page parsed as plain text")]
    internal static partial void DocumentOcrNoText(ILogger logger, int pageNumber);
}
