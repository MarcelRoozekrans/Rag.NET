namespace Rag.NET.Parsers.Pdf.Ocr;

/// <summary>
/// Seam for the compile-gated OCR engine: always compiled so the per-page fallback logic
/// in <see cref="PdfDocumentParser"/> is testable with a fake engine regardless of the
/// <c>&lt;EnableOcr&gt;</c> gate.
/// </summary>
internal interface IPdfOcrEngine
{
    /// <summary>
    /// Recognizes text in an encoded image (PNG or JPEG bytes).
    /// Returns <see langword="null"/> or an empty string when no text was found.
    /// </summary>
    string? Recognize(byte[] imageBytes);
}
