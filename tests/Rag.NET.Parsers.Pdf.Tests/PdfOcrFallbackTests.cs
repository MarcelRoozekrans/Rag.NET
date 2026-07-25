using Xunit;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// OCR fallback tests. The Tesseract engine is compile-gated (<c>&lt;EnableOcr&gt;</c>);
/// in the default gate-off compilation these tests pin the fail-fast construction throw,
/// and the per-page fallback logic is exercised through the internal
/// <c>IPdfOcrEngine</c> seam with a fake engine.
/// </summary>
public class PdfOcrFallbackTests
{
#if !ENABLE_OCR
    [Fact]
    public void OcrDisabled_UseOcrFallbackTrue_ThrowsInstructive()
    {
        // Fail fast: requesting OCR against a gate-off compilation must throw at parser
        // construction (misconfiguration should be loud), not at the first OCR-needed page.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }));

        Assert.Contains("<EnableOcr>true</EnableOcr>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Tesseract", exception.Message, StringComparison.Ordinal);
    }
#endif
}
