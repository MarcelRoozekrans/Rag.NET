using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Rag.NET.Parsers.Epub;
using Rag.NET.Parsers.Excel;
using Rag.NET.Parsers.Html;
using Rag.NET.Parsers.Pdf;
using Rag.NET.Parsers.PowerPoint;
using Rag.NET.Parsers.Word;
using Xunit;
using Drawing = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

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
    public async Task WordParser_ParsesGeneratedDocx_ReturnsNonEmptySections()
    {
        var sut = new WordDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("word-1"),
            FileName = "sample.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        };

        using var stream = CreateDocx("Integration Test Document", "This document is used for integration testing.");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
        Assert.Contains(sections, s => s.Text.Contains("Integration Test Document", StringComparison.Ordinal));
    }

    // ── Excel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExcelParser_ParsesGeneratedXlsx_ReturnsNonEmptySections()
    {
        var sut = new ExcelDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("excel-1"),
            FileName = "sample.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        };

        using var stream = CreateXlsx("Sheet1", [["Title", "Description"], ["Integration Test Document", "This document is used for integration testing."]]);
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    [Fact]
    public async Task ExcelParser_ParsesSharedStringXlsx_ReturnsNonEmptySections()
    {
        var sut = new ExcelDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("excel-2"),
            FileName = "sample-shared.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        };

        using var stream = CreateXlsxWithSharedStrings();
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    // ── PowerPoint ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PowerPointParser_ParsesGeneratedPptx_ReturnsNonEmptySections()
    {
        var sut = new PowerPointDocumentParser();
        var meta = new DocumentMetadata
        {
            DocumentId = new DocumentId("pptx-1"),
            FileName = "sample.pptx",
            ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        };

        using var stream = CreatePptx("Integration Test Document", "This document is used for integration testing.");
        var sections = await sut.ParseAsync(stream, meta, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    //
    // Fixture provenance (Resources/):
    // - sample.epub  — generated by the in-code EPUB builder mirrored in
    //   tests/Rag.NET.Parsers.Epub.Tests/EpubDocumentParserTests.CreateEpub (write its output to disk to regenerate).
    // - sample.eml   — hand-written minimal RFC 5322 message; edit the text file directly.
    // - sample.msg   — generated by the in-code CFB builder mirrored in
    //   tests/Rag.NET.Parsers.Email.Tests/MsgFixtureBuilder (write its output to disk to regenerate).
    // - sample.html / sample.pdf — pre-existing static fixtures.
    // - sample-table.pdf — generated by the in-code PdfPig PdfDocumentBuilder mirrored in
    //   tests/Rag.NET.Parsers.Pdf.Tests/TableFixtureGenerator (write its output to disk to regenerate).

    private static Stream OpenResource(string fileName)
    {
        var assembly = typeof(DocumentParserTests).Assembly;
        var resourceName = $"Rag.NET.Parsers.IntegrationTests.Resources.{fileName}";
        var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        return stream;
    }

    private static MemoryStream CreateDocx(string heading, string body)
    {
        var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new W.Document(
                new W.Body(
                    new W.Paragraph(new W.Run(new W.Text(heading))),
                    new W.Paragraph(new W.Run(new W.Text(body)))));
            mainPart.Document.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateXlsx(string sheetName, string[][] rows)
    {
        var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var docSheets = workbookPart.Workbook.GetFirstChild<Sheets>()!;

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();

            uint rowIndex = 1;
            foreach (var row in rows)
            {
                var sheetRow = new Row { RowIndex = rowIndex };
                int colIndex = 0;
                foreach (var cellValue in row)
                {
                    var colLetter = (char)('A' + colIndex);
                    sheetRow.AppendChild(new Cell
                    {
                        CellReference = $"{colLetter}{rowIndex}",
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(cellValue)),
                    });
                    colIndex++;
                }
                sheetData.AppendChild(sheetRow);
                rowIndex++;
            }

            worksheetPart.Worksheet = new Worksheet(sheetData);
            worksheetPart.Worksheet.Save();

            docSheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = sheetName,
            });

            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateXlsxWithSharedStrings()
    {
        var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var docSheets = workbookPart.Workbook.GetFirstChild<Sheets>()!;

            // Build shared string table with two entries: index 0 = "Header", index 1 = "Integration Test data"
            var sharedStringPart = workbookPart.AddNewPart<SharedStringTablePart>();
            sharedStringPart.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("Header")),
                new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("Integration Test data")));
            sharedStringPart.SharedStringTable.Save();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData(
                // Row 1: header row using shared string index 0
                new Row(new Cell
                {
                    CellReference = "A1",
                    DataType = CellValues.SharedString,
                    CellValue = new CellValue("0"),
                }) { RowIndex = 1U },
                // Row 2: data row using shared string index 1
                new Row(new Cell
                {
                    CellReference = "A2",
                    DataType = CellValues.SharedString,
                    CellValue = new CellValue("1"),
                }) { RowIndex = 2U });

            worksheetPart.Worksheet = new Worksheet(sheetData);
            worksheetPart.Worksheet.Save();

            docSheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "SharedStrings",
            });

            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreatePptx(string title, string body)
    {
        var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            var slideIdList = new P.SlideIdList();
            var slideLayoutIdList = new P.SlideLayoutIdList();
            var sldSz = new P.SlideSize { Cx = 9144000, Cy = 6858000, Type = P.SlideSizeValues.Screen4x3 };
            var notesSz = new P.NotesSize { Cx = 6858000, Cy = 9144000 };

            AddSlideMasterParts(presentationPart);

            var slidePart = presentationPart.AddNewPart<SlidePart>();
            BuildSlide(slidePart, title, body);

            slideIdList.Append(new P.SlideId
            {
                Id = 256U,
                RelationshipId = presentationPart.GetIdOfPart(slidePart),
            });

            presentationPart.Presentation.Append(slideIdList, slideLayoutIdList, sldSz, notesSz);
            presentationPart.Presentation.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
            presentationPart.Presentation.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            presentationPart.Presentation.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddSlideMasterParts(PresentationPart presentationPart)
    {
        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        slideLayoutPart.SlideLayout = new P.SlideLayout(new P.CommonSlideData(new P.ShapeTree()));
        slideLayoutPart.SlideLayout.Save();
        slideMasterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree()),
            new P.SlideLayoutIdList(new P.SlideLayoutId
            {
                Id = 2049U,
                RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart),
            }));
        slideMasterPart.SlideMaster.Save();
    }

    private static void BuildSlide(SlidePart slidePart, string title, string body)
    {
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 0U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new Drawing.TransformGroup()),
            BuildTextShape(1U, "Title 1", title),
            BuildTextShape(2U, "Content 2", body));

        slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
        slidePart.Slide.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        slidePart.Slide.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        slidePart.Slide.Save();
    }

    private static P.Shape BuildTextShape(uint id, string name, string text) =>
        new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(),
            new P.TextBody(
                new Drawing.BodyProperties(),
                new Drawing.ListStyle(),
                new Drawing.Paragraph(new Drawing.Run(new Drawing.Text(text)))));
}
