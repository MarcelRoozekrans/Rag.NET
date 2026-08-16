using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Rag.NET.Parsers.Epub;
using Rag.NET.Parsers.Excel;
using Rag.NET.Parsers.Html;
using Rag.NET.Parsers.Pdf;
using Rag.NET.Parsers.PowerPoint;
using Rag.NET.Parsers.Word;
using Xunit;

namespace Rag.NET.Parsers.IntegrationTests;

public sealed class DocumentParserTests
{
    // ── HTML ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HtmlParser_ParsesEmbeddedHtmlFile_ReturnsNonEmptySections()
    {
        var sut = new HtmlDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("html-1"),
            FileName = "sample.html",
            ContentType = "text/html",
        };

        await using var stream = OpenResource("sample.html");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
    }

    // ── EPUB ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EpubParser_ParsesEmbeddedEpubFile_ReturnsNonEmptySections()
    {
        var sut = new EpubDocumentParser(new HtmlDocumentParser());
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("epub-1"),
            FileName = "sample.epub",
            ContentType = "application/epub+zip",
        };

        await using var stream = OpenResource("sample.epub");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains("Second Chapter", StringComparison.Ordinal));
    }

    // ── Email (EML) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EmailParser_ParsesEmbeddedEmlFile_ReturnsNonEmptySections()
    {
        var sut = new EmailDocumentParser([], new HtmlDocumentParser());
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("eml-1"),
            FileName = "sample.eml",
            ContentType = "message/rfc822",
        };

        await using var stream = OpenResource("sample.eml");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
    }

    // ── Email (MSG) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MsgParser_ParsesEmbeddedMsgFile_ReturnsNonEmptySections()
    {
        var sut = new MsgDocumentParser([], new HtmlDocumentParser());
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("msg-1"),
            FileName = "sample.msg",
            ContentType = "application/vnd.ms-outlook",
        };

        await using var stream = OpenResource("sample.msg");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains("integration testing", StringComparison.Ordinal));
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PdfParser_ParsesEmbeddedPdfFile_ReturnsNonEmptySections()
    {
        var sut = new PdfDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("pdf-1"),
            FileName = "sample.pdf",
            ContentType = "application/pdf",
        };

        await using var stream = OpenResource("sample.pdf");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));

        // Phase 6.2: a content assertion, not merely "did not throw". This test asserted only
        // non-emptiness until 2026-08-16 — the shape that let the default chunker emit one chunk
        // per word while every test in the repository stayed green.
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PdfParser_ParsesEmbeddedTablePdf_EmitsTableSection()
    {
        var sut = new PdfDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("pdf-2"),
            FileName = "sample-table.pdf",
            ContentType = "application/pdf",
        };

        await using var stream = OpenResource("sample-table.pdf");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        var table = Assert.Single(sections, s => string.Equals(s.Heading, "table", StringComparison.Ordinal));
        Assert.Contains("| Alice | 30 | Paris |", table.Text, StringComparison.Ordinal);
        Assert.Contains(sections, s => s.Heading is null && s.Text.Contains("Introduction", StringComparison.Ordinal));
    }

    // ── Word ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WordParser_ParsesRealWordDocx_ReturnsNonEmptySections()
    {
        var sut = new WordDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("word-1"),
            FileName = "sample.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        };

        await using var stream = OpenResource("sample.docx");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Text.Contains("Produced by Microsoft Word", StringComparison.Ordinal));
    }

    // ── Excel ────────────────────────────────────────────────────────────────

    /// <remarks>
    /// This one test replaces the two it succeeded. The second was
    /// <c>ExcelParser_ParsesSharedStringXlsx</c>, which hand-built a shared-string table to prove
    /// the parser reads cells stored by reference rather than inline. It is not needed: **real
    /// Excel writes shared strings by default**, so <c>sample.xlsx</c> ships an
    /// <c>xl/sharedStrings.xml</c> with all six of its strings interned, and the assertions below
    /// cross that path on the way to every value. A real file covering a feature beats a synthetic
    /// file built to demonstrate it.
    /// </remarks>
    [Fact]
    public async Task ExcelParser_ParsesRealExcelXlsx_ReturnsNonEmptySections()
    {
        var sut = new ExcelDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("excel-1"),
            FileName = "sample.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        };

        await using var stream = OpenResource("sample.xlsx");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));

        var text = string.Join('\n', sections.Select(s => s.Text));
        Assert.Contains("Integration Test Document", text, StringComparison.Ordinal);
        Assert.Contains("Alice", text, StringComparison.Ordinal);
        Assert.Contains("Paris", text, StringComparison.Ordinal);
    }

    // ── PowerPoint ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PowerPointParser_ParsesRealPowerPointPptx_ReturnsNonEmptySections()
    {
        var sut = new PowerPointDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("pptx-1"),
            FileName = "sample.pptx",
            ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        };

        await using var stream = OpenResource("sample.pptx");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));

        var text = string.Join('\n', sections.Select(s => s.Text));
        Assert.Contains("Integration Test Document", text, StringComparison.Ordinal);
        Assert.Contains("integration testing", text, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    //
    // Fixture provenance (Resources/). Phase 6.2's bar is that a fixture must not be produced by
    // the library that parses it — a file DocumentFormat.OpenXml wrote and DocumentFormat.OpenXml
    // reads only proves the library round-trips itself. Recorded per file, because this repository
    // has been wrong about a file's origin or licence three times:
    //
    // - sample.docx / sample.xlsx / sample.pptx — produced 2026-08-16 by the REAL Microsoft Office
    //   applications (Word, Excel, PowerPoint; app.xml records Application and AppVersion 16) via
    //   COM automation, then scrubbed. Before 2026-08-16 these three formats had no fixture at all:
    //   the tests built a DOCX/XLSX/PPTX in-process with DocumentFormat.OpenXml and handed it
    //   straight back to a DocumentFormat.OpenXml parser. Two things were scrubbed after saving,
    //   neither visible in the document and neither suppressible beforehand — Office stamps them at
    //   save time: cp:lastModifiedBy in docProps/core.xml (the operator's real name) and
    //   x15ac:absPath in xl/workbook.xml (the full local save path, which carries the Windows
    //   username). Every entry of all three packages was then scanned for both, and for any
    //   residual user-profile path, before commit. PowerPoint's docProps/thumbnail.jpeg is a
    //   render of the slide itself and is retained: deleting it left a dangling relationship that
    //   made PresentationDocument.Open throw, so the package is kept intact and the thumbnail
    //   scanned instead.
    // - sample.epub  — generated by the in-code EPUB builder mirrored in
    //   tests/Rag.NET.Parsers.Epub.Tests/EpubDocumentParserTests.CreateEpub (write its output to disk to regenerate).
    // - sample.eml   — hand-written minimal RFC 5322 message; edit the text file directly.
    // - sample.msg   — generated by the in-code CFB builder mirrored in
    //   tests/Rag.NET.Parsers.Email.Tests/MsgFixtureBuilder (write its output to disk to regenerate).
    // - sample.html  — pre-existing static fixture.
    // - sample.pdf   — pre-existing static fixture. It carries no /Producer or /Creator, so its
    //   origin is genuinely unrecorded; that is stated rather than guessed at.
    // - sample-table.pdf — generated by the in-code PdfPig PdfDocumentBuilder mirrored in
    //   tests/Rag.NET.Parsers.Pdf.Tests/TableFixtureGenerator (write its output to disk to regenerate).
    // - sample-scanned.pdf — generated by tests/Rag.NET.Parsers.Pdf.Tests/ScannedFixtureGenerator
    //   (embeds that project's Resources/ocr-sample.png; exercised by the Pdf tests' OCR suite —
    //   no matrix entry here because OCR needs the EnableOcr compile gate).
    //
    // Still self-produced, and therefore still owed a real fixture by Phase 6.2: epub, msg and
    // sample-table.pdf. They are weaker than the Office three were, not stronger — the reason they
    // are not fixed in the same change is that no independent producer for them exists on this
    // machine, where Word, Excel and PowerPoint did.

    private static Stream OpenResource(string fileName)
    {
        var assembly = typeof(DocumentParserTests).Assembly;
        var resourceName = $"Rag.NET.Parsers.IntegrationTests.Resources.{fileName}";
        var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        return stream;
    }
}
