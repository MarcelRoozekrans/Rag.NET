using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf.Ocr;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Document-level OCR tests: the whole-PDF seam (<c>IDocumentOcrEngine</c>) driven by a
/// hand-written fake. Nothing here needs the <c>&lt;EnableOcr&gt;</c> gate — the document-level
/// path is ungated by design, and the per-image Tesseract fallback is not involved.
/// </summary>
public class PdfDocumentOcrTests
{
    /// <summary>76 characters — comfortably above the default 50-character OCR threshold.</summary>
    private const string LongText = "This page has plenty of extracted text, well above the OCR trigger threshold.";

    /// <summary>Short but real text: below the threshold, and must never be lost.</summary>
    private const string ShortText = "Short page.";

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("pdf-dococr-1"),
        FileName = "test.pdf",
        ContentType = "application/pdf",
    };

    private static async Task<List<DocumentSection>> ParseAsync(PdfDocumentParser parser, Stream stream)
    {
        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        return sections;
    }

    /// <summary>Builds a PDF with one page per entry; an empty entry produces a page with no text layer.</summary>
    private static MemoryStream CreatePdf(params string[] pageTexts)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var pageText in pageTexts)
        {
            var page = builder.AddPage(PageSize.Letter, isPortrait: true);
            if (pageText.Length > 0)
            {
                _ = page.AddText(pageText, 10, new PdfPoint(40, 700), font);
            }
        }

        return new MemoryStream(builder.Build());
    }

    [Fact]
    public async Task DocumentOcr_SubThresholdPages_UseOcrText_OtherPagesKeepPdfPigText()
    {
        var engine = FakeDocumentOcrEngine.Returning(new Dictionary<int, string>
        {
            [1] = "AZURE PAGE ONE",
            [2] = "AZURE PAGE TWO",
            [3] = "AZURE PAGE THREE",
        }, billedPages: 3);
        var sut = new PdfDocumentParser(new PdfParserOptions(), logger: null, engine);
        using var stream = CreatePdf(LongText, string.Empty, LongText);

        var sections = await ParseAsync(sut, stream);

        Assert.Equal(3, sections.Count);

        // Pages 1 and 3 were read by PdfPig: its extraction is exact, so it wins even though
        // the engine returned text for them too.
        Assert.Null(sections[0].Heading);
        Assert.Contains("threshold", sections[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE", sections[0].Text, StringComparison.Ordinal);
        Assert.Null(sections[2].Heading);
        Assert.DoesNotContain("AZURE", sections[2].Text, StringComparison.Ordinal);

        // Page 2 had no text layer: the recognized text replaces it, keyed 1-based.
        Assert.Equal("ocr", sections[1].Heading);
        Assert.Equal("AZURE PAGE TWO", sections[1].Text);
        Assert.Equal(2, sections[1].PageNumber);

        Assert.Equal(1, engine.Calls);

        // SectionIndex stays contiguous across the substitution.
        for (int i = 0; i < sections.Count; i++)
        {
            Assert.Equal(i, sections[i].SectionIndex);
        }
    }

    [Fact]
    public async Task DocumentOcr_NoSubThresholdPages_EngineNeverCalled()
    {
        // COST-CRITICAL. A document PdfPig read in full must never reach a per-page priced
        // API. The assertion is an exact call count plus an empty call log — not "it did not
        // throw", which a broken parser would also satisfy.
        var engine = FakeDocumentOcrEngine.Returning(new Dictionary<int, string>
        {
            [1] = "AZURE MUST NOT APPEAR",
            [2] = "AZURE MUST NOT APPEAR",
        }, billedPages: 2);
        var sut = new PdfDocumentParser(new PdfParserOptions(), logger: null, engine);
        using var stream = CreatePdf(LongText, LongText);

        var sections = await ParseAsync(sut, stream);

        Assert.Equal(0, engine.Calls);
        Assert.Empty(engine.ReceivedPdfLengths);
        Assert.Equal(2, sections.Count);
        for (int i = 0; i < sections.Count; i++)
        {
            Assert.Null(sections[i].Heading);
            Assert.DoesNotContain("AZURE", sections[i].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DocumentOcr_CalledExactlyOncePerDocument()
    {
        // COST-CRITICAL. Four sub-threshold pages must produce ONE whole-document call, not
        // four page-sized ones — the difference between one billed document and four.
        var engine = FakeDocumentOcrEngine.Returning(new Dictionary<int, string>
        {
            [1] = "OCR 1",
            [2] = "OCR 2",
            [3] = "OCR 3",
            [4] = "OCR 4",
        }, billedPages: 4);
        var sut = new PdfDocumentParser(new PdfParserOptions(), logger: null, engine);
        using var stream = CreatePdf(string.Empty, string.Empty, string.Empty, string.Empty);

        var sections = await ParseAsync(sut, stream);

        Assert.Equal(1, engine.Calls);
        _ = Assert.Single(engine.ReceivedPdfLengths);
        Assert.Equal(4, sections.Count);
        for (int i = 0; i < sections.Count; i++)
        {
            Assert.Equal("ocr", sections[i].Heading);
            Assert.Equal($"OCR {i + 1}", sections[i].Text);
            Assert.Equal(i + 1, sections[i].PageNumber);
        }
    }

    [Fact]
    public async Task DocumentOcr_DocumentAboveMaxOcrPages_SkipsWithWarning()
    {
        // One recognised page, three billed: the gap the cap exists to bound.
        var engine = FakeDocumentOcrEngine.Returning(new Dictionary<int, string>
        {
            [2] = "AZURE MUST NOT APPEAR",
        }, billedPages: 3);
        var logger = new CapturingLogger<PdfDocumentParser>();
        var sut = new PdfDocumentParser(new PdfParserOptions { MaxOcrPages = 2 }, logger, engine);
        using var stream = CreatePdf(LongText, string.Empty, LongText);

        var sections = await ParseAsync(sut, stream);

        Assert.Equal(0, engine.Calls);

        // The document is unchanged from a no-engine parse: two text pages, the blank page
        // emitting nothing (today's empty-page behavior).
        Assert.Equal(2, sections.Count);
        for (int i = 0; i < sections.Count; i++)
        {
            Assert.DoesNotContain("AZURE", sections[i].Text, StringComparison.Ordinal);
        }

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("MaxOcrPages", warning.Message, StringComparison.Ordinal);
        Assert.Contains("3 pages", warning.Message, StringComparison.Ordinal);
        Assert.Contains("cap of 2", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentOcr_EngineThrows_FallsBackToPlainTextLosslessly()
    {
        var engine = FakeDocumentOcrEngine.Throwing(new InvalidOperationException("engine failure"));
        var logger = new CapturingLogger<PdfDocumentParser>();
        var sut = new PdfDocumentParser(new PdfParserOptions(), logger, engine);
        using var stream = CreatePdf(ShortText, LongText);

        var sections = await ParseAsync(sut, stream);

        Assert.Equal(1, engine.Calls);

        // Lossless: the short-but-real page keeps its own text rather than being dropped,
        // and the page that never needed OCR is untouched.
        Assert.Equal(2, sections.Count);
        Assert.Null(sections[0].Heading);
        Assert.Contains("Short", sections[0].Text, StringComparison.Ordinal);
        Assert.Null(sections[1].Heading);
        Assert.Contains("threshold", sections[1].Text, StringComparison.Ordinal);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("Document-level OCR failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocumentOcr_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var engine = FakeDocumentOcrEngine.CancellingVia(cts);
        var logger = new CapturingLogger<PdfDocumentParser>();
        var sut = new PdfDocumentParser(new PdfParserOptions(), logger, engine);
        using var stream = CreatePdf(string.Empty, LongText);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var section in sut.ParseAsync(stream, CreateMetadata(), cts.Token))
            {
                Assert.NotNull(section);
            }
        });

        Assert.Equal(1, engine.Calls);

        // Cancellation is not a degradation: it must not be swallowed into a warning by the
        // degraded-never-broken catch.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void BothEnginesConfigured_ThrowsAtRegistration()
    {
        // Whichever call comes second throws — the combination is a configuration error, not
        // a precedence rule, so neither ordering may quietly succeed.
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);
        builder.AddPdfParser(o => o.UseOcrFallback = true);

        var tesseractFirst = Assert.Throws<InvalidOperationException>(() =>
            builder.UseDocumentOcrEngine(FakeDocumentOcrEngine.Empty()));
        Assert.Contains("UseDocumentOcrEngine", tesseractFirst.Message, StringComparison.Ordinal);

        var reversedServices = new ServiceCollection();
        var reversedBuilder = new RagBuilder(reversedServices);
        reversedBuilder.UseDocumentOcrEngine(FakeDocumentOcrEngine.Empty());

        var documentFirst = Assert.Throws<InvalidOperationException>(() =>
            reversedBuilder.AddPdfParser(o => o.UseOcrFallback = true));
        Assert.Contains("UseDocumentOcrEngine", documentFirst.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UseDocumentOcrEngine_ResolvedParser_RoutesThroughTheEngine(bool configureOptions)
    {
        // Both AddPdfParser overloads must reach the engine: the configuring one wires it
        // explicitly, the parameterless one relies on constructor injection into the
        // optional documentOcrEngine parameter.
        var engine = FakeDocumentOcrEngine.Returning(
            new Dictionary<int, string> { [1] = "OCR ONE" }, billedPages: 2);
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);
        if (configureOptions)
        {
            builder.AddPdfParser(o => o.ExtractTables = false);
        }
        else
        {
            builder.AddPdfParser();
        }

        builder.UseDocumentOcrEngine(engine);

        using var provider = services.BuildServiceProvider();
        var parser = Assert.IsType<PdfDocumentParser>(
            Assert.Single(provider.GetServices<IDocumentParser>()));
        using var stream = CreatePdf(string.Empty, LongText);

        var sections = await ParseAsync(parser, stream);

        Assert.Equal(1, engine.Calls);
        Assert.Equal("ocr", sections[0].Heading);
        Assert.Equal("OCR ONE", sections[0].Text);
    }

    [Fact]
    public async Task NonSeekableStream_HandledPerTheDocumentedChoice()
    {
        // The documented choice is to buffer, not refuse. RagError.NonSeekableStream is the
        // ingestor's pre-flight signal and IDocumentParser.ParseAsync has no channel for it;
        // refusing here would mean throwing, against the parser's degraded-never-broken
        // posture.
        using var seekable = CreatePdf(string.Empty, LongText);
        var bytes = seekable.ToArray();
        var engine = FakeDocumentOcrEngine.Returning(
            new Dictionary<int, string> { [1] = "OCR ONE" }, billedPages: 2);
        var sut = new PdfDocumentParser(new PdfParserOptions(), logger: null, engine);
        using var nonSeekable = new ForwardOnlyStream(bytes);

        var sections = await ParseAsync(sut, nonSeekable);

        Assert.Equal(1, engine.Calls);

        // The engine received the complete PDF — not a stream PdfPig had already drained.
        Assert.Equal(bytes.Length, Assert.Single(engine.ReceivedPdfLengths));
        Assert.Equal(2, sections.Count);
        Assert.Equal("ocr", sections[0].Heading);
        Assert.Equal("OCR ONE", sections[0].Text);
        Assert.Null(sections[1].Heading);
    }

    [Fact]
    public async Task NonSeekableStream_DefaultPath_AcceptsItToo()
    {
        // Pins the symmetry the buffering decision rests on: PdfPig takes a non-seekable
        // stream on the default path, so buffering on the document-level path keeps the two
        // behaviourally identical rather than making one reject input the other handles. If
        // this ever fails, BufferAsync's justification needs rewriting, not just this test.
        using var seekable = CreatePdf(ShortText, LongText);
        var bytes = seekable.ToArray();
        var sut = new PdfDocumentParser();
        using var nonSeekable = new ForwardOnlyStream(bytes);

        var sections = await ParseAsync(sut, nonSeekable);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Short", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("threshold", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentOcrResult_GuardsItsArguments()
    {
        // Public API: an engine in another assembly is the caller, so the guards are the
        // contract rather than an internal courtesy.
        _ = Assert.Throws<ArgumentNullException>(() => new DocumentOcrResult(null!, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentOcrResult(new Dictionary<int, string>(), -1));

        var result = new DocumentOcrResult(new Dictionary<int, string> { [1] = "text" }, BilledPages: 500);
        Assert.Equal(500, result.BilledPages);

        // BilledPages counts submitted pages, not recognized ones — they are not the same
        // number, and the expensive case is exactly where they diverge.
        _ = Assert.Single(result.PageText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPdfParser_MaxOcrPagesBelowOne_Throws(int maxOcrPages)
    {
        // A cap of zero or less would either disable OCR by accident or mean nothing at all;
        // either way it is a configuration mistake, and configuration mistakes stay loud.
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.AddPdfParser(o => o.MaxOcrPages = maxOcrPages));
    }

    /// <summary>A read-only, forward-only stream: <c>CanSeek</c> is false and seeking throws.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
