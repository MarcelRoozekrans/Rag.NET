using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Parsers.Excel.Tests;

public class ExcelDocumentParserTests
{
    private readonly ExcelDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.xlsx"
    };

    [Fact]
    public void CanParse_Xlsx_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    [Fact]
    public void CanParse_Pdf_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/pdf"));
    }

    [Fact]
    public async Task ParseAsync_BasicSheet_ReturnsSectionPerRow()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["Sheet1"] =
            [
                ["Name", "Age"],
                ["Alice", "30"],
                ["Bob", "25"],
            ]
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Name: Alice | Age: 30", sections[0].Text);
        Assert.Equal("Name: Bob | Age: 25", sections[1].Text);
    }

    [Fact]
    public async Task ParseAsync_SetsSheetNameAsHeading()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["Employees"] =
            [
                ["Name"],
                ["Alice"],
            ]
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Equal("Employees", sections[0].Heading);
    }

    [Fact]
    public async Task ParseAsync_MultipleSheets_ProcessesAll()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["Sheet1"] = [["Col"], ["Val1"]],
            ["Sheet2"] = [["Col"], ["Val2"]],
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Sheet1", sections[0].Heading);
        Assert.Equal("Sheet2", sections[1].Heading);
    }

    [Fact]
    public async Task ParseAsync_EmptySheet_ReturnsNoSections()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["Sheet1"] = []
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        using var stream = CreateXlsx(new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["Sheet1"] = [["C"], ["A"], ["B"]],
        });

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    private static MemoryStream CreateXlsx(Dictionary<string, string[][]> sheets)
    {
        var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var docSheets = workbookPart.Workbook.GetFirstChild<Sheets>()!;

            uint sheetId = 1;
            foreach (var (sheetName, rows) in sheets)
            {
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
                            InlineString = new InlineString(new Text(cellValue)),
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
                    SheetId = sheetId++,
                    Name = sheetName,
                });
            }

            workbookPart.Workbook.Save();
        }
        stream.Position = 0;
        return stream;
    }
}
