namespace Rag.NET.Parsers.Pdf.Ocr;

// Deliberate duplication of Rag.NET.Parsers.Vision's Tesseract usage (ImageDocumentParser.TryOcr):
// the parser packages stay decoupled; a future shared OCR package is the consolidation path.
#if ENABLE_OCR
/// <summary>
/// Tesseract-backed OCR engine, compiled only under <c>&lt;EnableOcr&gt;true&lt;/EnableOcr&gt;</c>.
/// Holds one <see cref="Tesseract.TesseractEngine"/> for the owning parser's lifetime
/// (engine construction is expensive; the parser is a DI singleton). Tesseract engines are
/// NOT thread-safe — the parser serializes calls to <see cref="Recognize"/>.
/// </summary>
internal sealed class TesseractOcrEngine : IPdfOcrEngine, IDisposable
{
    private readonly Tesseract.TesseractEngine _engine;

    public TesseractOcrEngine(PdfParserOptions options)
    {
        _engine = new Tesseract.TesseractEngine(
            options.TessDataPath, options.OcrLanguage, Tesseract.EngineMode.Default);
    }

    public string? Recognize(byte[] imageBytes)
    {
        using var pix = Tesseract.Pix.LoadFromMemory(imageBytes);
        using var page = _engine.Process(pix);
        return page.GetText()?.Trim();
    }

    public void Dispose() => _engine.Dispose();
}
#else
/// <summary>
/// Gate-off stub: constructing it (which <see cref="PdfDocumentParser"/> does when
/// <see cref="PdfParserOptions.UseOcrFallback"/> is enabled) throws an instructive
/// <see cref="InvalidOperationException"/> so misconfiguration fails fast at construction.
/// </summary>
internal sealed class TesseractOcrEngine : IPdfOcrEngine
{
    private const string GateMessage =
        "OCR support requires the Tesseract package. Add <EnableOcr>true</EnableOcr> to your " +
        "project file to enable it, and provide a tessdata directory with the configured " +
        "language data (see PdfParserOptions.TessDataPath and PdfParserOptions.OcrLanguage).";

    public TesseractOcrEngine(PdfParserOptions options)
    {
        _ = options;
        throw new InvalidOperationException(GateMessage);
    }

    public string? Recognize(byte[] imageBytes) => throw new InvalidOperationException(GateMessage);
}
#endif
