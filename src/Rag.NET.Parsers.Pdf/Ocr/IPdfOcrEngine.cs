namespace Rag.NET.Parsers.Pdf.Ocr;

/// <summary>
/// Seam for the compile-gated, per-image OCR engine: always compiled so the per-page
/// fallback logic in <see cref="PdfDocumentParser"/> is testable with a fake engine
/// regardless of the <c>&lt;EnableOcr&gt;</c> gate.
/// <para>
/// It is not the only OCR seam: <see cref="IDocumentOcrEngine"/> is its sibling, for engines
/// that take the whole PDF and return every page from one asynchronous call. This one stays
/// synchronous, token-less and per-image because that is the shape a local native library
/// needs.
/// </para>
/// </summary>
internal interface IPdfOcrEngine
{
    /// <summary>
    /// Recognizes text in an encoded image (PNG or JPEG bytes).
    /// Returns <see langword="null"/> or an empty string when no text was found.
    /// </summary>
    string? Recognize(byte[] imageBytes);
}
