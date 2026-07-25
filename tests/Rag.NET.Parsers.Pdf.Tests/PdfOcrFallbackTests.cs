using Microsoft.Extensions.Logging;
using Rag.NET.Models;
using UglyToad.PdfPig.Content;
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

    /// <summary>A one-page PDF with no text and no images (OCR fallback finds nothing to do).</summary>
    private static MemoryStream CreateEmptyPagePdf()
    {
        using var builder = new PdfDocumentBuilder();
        _ = builder.AddPage(PageSize.Letter, isPortrait: true);
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
