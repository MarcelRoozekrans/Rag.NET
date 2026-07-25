namespace Rag.NET.Parsers.Pdf.Ocr;

/// <summary>Creates the OCR engine backing <see cref="PdfParserOptions.UseOcrFallback"/>.</summary>
internal static class PdfOcrEngineFactory
{
    /// <summary>
    /// Creates the Tesseract-backed engine. In a gate-off compilation
    /// (no <c>&lt;EnableOcr&gt;true&lt;/EnableOcr&gt;</c>) the stub engine's constructor
    /// throws an instructive <see cref="InvalidOperationException"/> — callers invoke this
    /// at parser construction so misconfiguration fails fast.
    /// </summary>
    internal static IPdfOcrEngine Create(PdfParserOptions options) => new TesseractOcrEngine(options);
}
