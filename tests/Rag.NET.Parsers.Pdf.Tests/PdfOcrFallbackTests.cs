using Microsoft.Extensions.Logging;
using Rag.NET.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// OCR fallback tests. The Tesseract engine is compile-gated (<c>&lt;EnableOcr&gt;</c>);
/// in the default gate-off compilation these tests pin the fail-fast construction throw,
/// and the per-page fallback logic is exercised through the internal
/// <c>IPdfOcrEngine</c> seam with a fake engine. The real-Tesseract integration test at the
/// bottom only exists when building with <c>/p:EnableOcr=true</c> — the default (CI) build
/// compiles it away — and is additionally env-gated on <c>RAGNET_TESSDATA</c>.
/// </summary>
public class PdfOcrFallbackTests
{
    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("pdf-ocr-1"),
        FileName = "test.pdf",
        ContentType = "application/pdf",
    };

    private static Stream OpenResource(string fileName)
    {
        var assembly = typeof(PdfOcrFallbackTests).Assembly;
        var resourceName = $"Rag.NET.Parsers.Pdf.Tests.Resources.{fileName}";
        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
    }

    private static async Task<List<DocumentSection>> ParseAsync(PdfDocumentParser parser, Stream stream)
    {
        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        return sections;
    }

    /// <summary>A 1x1 transparent PNG — a valid image whose re-encoded bytes stay tiny.</summary>
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>A one-page PDF with no text and no images (OCR fallback finds nothing to do).</summary>
    private static MemoryStream CreateEmptyPagePdf()
    {
        using var builder = new PdfDocumentBuilder();
        _ = builder.AddPage(PageSize.Letter, isPortrait: true);
        return new MemoryStream(builder.Build());
    }

    /// <summary>A one-page PDF with short real text (below the default threshold) plus an image.</summary>
    private static MemoryStream CreateShortTextWithImagePdf()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.Letter, isPortrait: true);
        _ = page.AddText("Short scanned page", 12, new PdfPoint(100, 700), font);
        _ = page.AddPng(ScannedFixtureGenerator.ReadPngResource(), new PdfRectangle(56, 400, 556, 517));
        return new MemoryStream(builder.Build());
    }

    /// <summary>
    /// A one-page PDF with two images: the tiny 1x1 PNG displayed LARGE (500x500 — must be
    /// tried first, ordering is by display area) and the 600x140 text PNG displayed small.
    /// </summary>
    private static MemoryStream CreateTwoImagePdf()
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.Letter, isPortrait: true);
        _ = page.AddPng(TinyPng, new PdfRectangle(50, 200, 550, 700));
        _ = page.AddPng(ScannedFixtureGenerator.ReadPngResource(), new PdfRectangle(50, 50, 150, 73));
        return new MemoryStream(builder.Build());
    }

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

    // ── Per-page fallback via the internal fake-engine seam ──────────────────

    [Fact]
    public async Task OcrFallback_ShortPage_EngineInvoked_SectionEmitted()
    {
        var engine = new FakePdfOcrEngine("Recognized text");
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger: null, engine);
        await using var stream = OpenResource("sample-scanned.pdf");

        var sections = await ParseAsync(sut, stream);

        var section = Assert.Single(sections);
        Assert.Equal("ocr", section.Heading);
        Assert.Equal("Recognized text", section.Text);
        Assert.Equal(1, section.PageNumber);
        Assert.Equal(0, section.SectionIndex);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task OcrFallback_LongPage_EngineNotInvoked()
    {
        // sample.pdf's page has 25 characters of text; a threshold below that must leave
        // the page on the normal (non-OCR) path.
        var engine = new FakePdfOcrEngine("should never appear");
        var sut = new PdfDocumentParser(
            new PdfParserOptions { UseOcrFallback = true, OcrMinCharacters = 10 }, logger: null, engine);
        await using var stream = OpenResource("sample.pdf");

        var sections = await ParseAsync(sut, stream);

        Assert.NotEmpty(sections);
        Assert.DoesNotContain(sections, s => string.Equals(s.Heading, "ocr", StringComparison.Ordinal));
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task OcrFallback_NoImages_WarnsAndSkips()
    {
        var engine = new FakePdfOcrEngine("should never appear");
        var logger = new CapturingLogger<PdfDocumentParser>();
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger, engine);
        using var stream = CreateEmptyPagePdf();

        var sections = await ParseAsync(sut, stream);

        Assert.Empty(sections);
        Assert.Equal(0, engine.Calls);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("no embedded images", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OcrFallback_EngineReturnsEmpty_SkipsPage()
    {
        var engine = new FakePdfOcrEngine(string.Empty);
        var logger = new CapturingLogger<PdfDocumentParser>();
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger, engine);
        await using var stream = OpenResource("sample-scanned.pdf");

        var sections = await ParseAsync(sut, stream);

        Assert.Empty(sections);
        Assert.Equal(1, engine.Calls);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("no text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OcrFallback_EngineThrows_WarnsAndSkipsPage()
    {
        var engine = new FakePdfOcrEngine(new InvalidOperationException("engine failure"));
        var logger = new CapturingLogger<PdfDocumentParser>();
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger, engine);
        await using var stream = OpenResource("sample-scanned.pdf");

        var sections = await ParseAsync(sut, stream);

        Assert.Empty(sections);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("OCR fallback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OcrFallback_NoImages_ShortRealTextPreserved()
    {
        // Lossless: sample.pdf's page has 25 chars of real text and no images. A 30-char
        // threshold sends it down the OCR path, which finds no images — the original
        // plain-text section must still be emitted; enabling OCR must never lose text the
        // parser could already extract.
        var engine = new FakePdfOcrEngine("should never appear");
        var sut = new PdfDocumentParser(
            new PdfParserOptions { UseOcrFallback = true, OcrMinCharacters = 30 }, logger: null, engine);
        await using var stream = OpenResource("sample.pdf");

        var sections = await ParseAsync(sut, stream);

        var section = Assert.Single(sections);
        Assert.Null(section.Heading);
        Assert.False(string.IsNullOrWhiteSpace(section.Text));
        Assert.Equal(0, engine.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OcrFallback_FailedOcr_ShortRealTextPreserved(bool engineThrows)
    {
        // Lossless: a page with short real text AND an image triggers OCR; when the engine
        // yields nothing (or fails), the page falls back to its plain-text section instead
        // of being dropped.
        var engine = engineThrows
            ? new FakePdfOcrEngine(new InvalidOperationException("engine failure"))
            : new FakePdfOcrEngine(string.Empty);
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger: null, engine);
        using var stream = CreateShortTextWithImagePdf();

        var sections = await ParseAsync(sut, stream);

        var section = Assert.Single(sections);
        Assert.Null(section.Heading);
        Assert.Contains("Short", section.Text, StringComparison.Ordinal);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task OcrFallback_MultipleImages_LargestDisplayFirst_UntilOneYieldsText()
    {
        // Ordering is by display area, not byte size: the tiny 1x1 PNG shown at 500x500 is
        // tried first (and yields nothing), then the small-displayed text PNG succeeds.
        var engine = new FakePdfOcrEngine(bytes => bytes.Length < 1000 ? string.Empty : "Recognized second");
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger: null, engine);
        using var stream = CreateTwoImagePdf();

        var sections = await ParseAsync(sut, stream);

        var section = Assert.Single(sections);
        Assert.Equal("ocr", section.Heading);
        Assert.Equal("Recognized second", section.Text);
        Assert.Equal(2, engine.Calls);
        // First call received the 1x1 image's (tiny) bytes: largest display area first.
        Assert.True(engine.ByteLengths[0] < engine.ByteLengths[1]);
    }

    [Fact]
    public async Task OcrFallback_Disabled_NeverInvoked()
    {
        // Default options: UseOcrFallback = false. The scanned page has no text, so it is
        // skipped exactly like today's empty-page behavior — the engine must not run.
        var engine = new FakePdfOcrEngine("should never appear");
        var sut = new PdfDocumentParser(options: null, logger: null, engine);
        await using var stream = OpenResource("sample-scanned.pdf");

        var sections = await ParseAsync(sut, stream);

        Assert.Empty(sections);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task ConcurrentParse_TwoDocuments_NoTornEngineState()
    {
        // Documents parse in parallel (Phase 1.3 batch optimiser) against the singleton
        // parser; Tesseract engines are not thread-safe, so the parser must serialize
        // Recognize calls. The fake engine flags any overlapping invocation.
        var engine = new FakePdfOcrEngine("Recognized text");
        var sut = new PdfDocumentParser(new PdfParserOptions { UseOcrFallback = true }, logger: null, engine);

        const int parallelism = 4;
        var tasks = new List<Task<List<DocumentSection>>>(parallelism);
        for (int i = 0; i < parallelism; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await using var stream = OpenResource("sample-scanned.pdf");
                return await ParseAsync(sut, stream);
            }));
        }

        var results = await Task.WhenAll(tasks);

        Assert.False(engine.SawOverlap);
        Assert.Equal(parallelism, engine.Calls);
        Assert.All(results, sections => Assert.Single(sections));
    }

    [Fact]
    public async Task ScannedFixture_MatchesGenerator()
    {
        // Ties the checked-in sample-scanned.pdf to ScannedFixtureGenerator: regenerated
        // bytes must parse to the same sections (byte-identity is not required).
        var sut = new PdfDocumentParser(
            new PdfParserOptions { UseOcrFallback = true }, logger: null, new FakePdfOcrEngine("Recognized text"));
        await using var fixtureStream = OpenResource("sample-scanned.pdf");
        var fromFixture = await ParseAsync(sut, fixtureStream);
        using var generatedStream = new MemoryStream(ScannedFixtureGenerator.Generate());
        var fromGenerator = await ParseAsync(sut, generatedStream);

        Assert.Equal(fromFixture, fromGenerator);
    }

#if ENABLE_OCR
    // Compiled only under /p:EnableOcr=true (never in the default/CI build) and skipped
    // unless RAGNET_TESSDATA points at a tessdata directory with eng.traineddata.
    [Fact]
    public async Task OcrFallback_RealTesseract_ReadsScannedFixture()
    {
        var tessData = Environment.GetEnvironmentVariable("RAGNET_TESSDATA");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(tessData),
            "Set RAGNET_TESSDATA to a tessdata directory (containing eng.traineddata) to run the real-Tesseract test.");

        var sut = new PdfDocumentParser(new PdfParserOptions
        {
            UseOcrFallback = true,
            TessDataPath = tessData!,
        });
        await using var stream = OpenResource("sample-scanned.pdf");

        var sections = await ParseAsync(sut, stream);

        var section = Assert.Single(sections);
        Assert.Equal("ocr", section.Heading);
        Assert.Contains("OCR Sample", section.Text, StringComparison.OrdinalIgnoreCase);
    }
#endif
}
