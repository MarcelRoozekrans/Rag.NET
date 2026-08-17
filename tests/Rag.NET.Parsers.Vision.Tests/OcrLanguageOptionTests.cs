using Rag.NET.Parsers.Vision;
using Xunit;

namespace Rag.NET.Parsers.Vision.Tests;

/// <summary>
/// Keeps the Vision parser's OCR settings present, defaulted, and in step with the PDF parser's.
/// </summary>
/// <remarks>
/// <para>
/// <b>#299.</b> <c>ImageDocumentParser.TryOcr</c> constructed Tesseract with two literals —
/// <c>@"./tessdata"</c> and <c>"eng"</c> — while <c>PdfParserOptions</c> had exposed both as
/// settings all along. A caller ingesting the same German document through both parsers could
/// configure one and not the other, and the Vision path gave no sign it was reading English only.
/// </para>
/// <para>
/// <b>What this can and cannot check.</b> The OCR body is inside <c>#if ENABLE_OCR</c>, which no
/// default build defines — the published package compiles Tesseract out so consumers do not carry
/// its native payload. So these tests cover the settings and their agreement with the PDF parser,
/// which is where the defect actually was and where drift would return. Driving a real engine needs
/// the OCR flavour and a tessdata directory, and is the fenced procedure in
/// <c>docs/reference/ci.md</c>.
/// </para>
/// <para>
/// The names and defaults deliberately match <c>PdfParserOptions</c>, so a reader who learns one has
/// learned the other. That agreement is asserted in <c>Rag.NET.RepoConventions.Tests</c> rather than
/// here — checking it from this project would mean referencing <c>Rag.NET.Parsers.Pdf</c> and its
/// native OCR dependencies to compare two strings.
/// </para>
/// </remarks>
public sealed class OcrLanguageOptionTests
{
    [Fact]
    public void TheVisionParserExposesBothOcrSettings()
    {
        var options = new ImageDescriptionOptions();

        Assert.Equal("eng", options.OcrLanguage, StringComparer.Ordinal);
        Assert.Equal("./tessdata", options.TessDataPath, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("deu")]
    [InlineData("jpn")]
    [InlineData("deu+eng")]
    [InlineData("chi_sim")]
    public void AnyTesseractLanguageCodeIsAccepted(string code)
    {
        // Including the multi-language `+` form Tesseract takes. Nothing validates the code against
        // the installed tessdata here, and that is correct: which .traineddata exists is a property
        // of the machine, not of the options, and Tesseract reports the mismatch at engine
        // construction — where TryOcr logs it and returns no text rather than throwing.
        var options = new ImageDescriptionOptions { OcrLanguage = code };

        Assert.Equal(code, options.OcrLanguage, StringComparer.Ordinal);
    }
}
