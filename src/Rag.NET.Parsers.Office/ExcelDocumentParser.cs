using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Excel;

public sealed class ExcelDocumentParser : IDocumentParser, IDeclaresContentTypes
{
    private const string SpreadsheetContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [SpreadsheetContentType];

    public bool CanParse(string contentType) =>
        contentType.Equals(SpreadsheetContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;

        if (workbookPart is null)
        {
            yield break;
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var workbook = workbookPart.Workbook;
        if (workbook is null)
        {
            yield break;
        }

        var sheets = workbook.Sheets?.Elements<Sheet>() ?? [];
        int sectionIndex = 0;

        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (var section in ProcessSheetAsync(workbookPart, sheet, sharedStrings, metadata, sectionIndex, cancellationToken).ConfigureAwait(false))
            {
                sectionIndex = section.SectionIndex + 1;
                yield return section;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<DocumentSection> ProcessSheetAsync(
        WorkbookPart workbookPart,
        Sheet sheet,
        SharedStringTable? sharedStrings,
        DocumentMetadata metadata,
        int startIndex,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (sheet.Id?.Value is null)
        {
            yield break;
        }

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
        var worksheet = worksheetPart.Worksheet;
        if (worksheet is null)
        {
            yield break;
        }

        var sheetData = worksheet.GetFirstChild<SheetData>();

        if (sheetData is null)
        {
            yield break;
        }

        var rows = sheetData.Elements<Row>().ToList();
        if (rows.Count < 2)
        {
            yield break;
        }

        var headers = GetRowValues(rows[0], sharedStrings);
        var sheetName = sheet.Name?.Value;
        int sectionIndex = startIndex;

        for (int i = 1; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = FormatRow(rows[i], headers, sharedStrings);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new DocumentSection
                {
                    Text = text,
                    DocumentId = metadata.DocumentId,
                    SectionIndex = sectionIndex++,
                    Heading = sheetName,
                };
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string FormatRow(Row row, List<string> headers, SharedStringTable? sharedStrings)
    {
        var values = GetRowValues(row, sharedStrings);
        var pairs = new List<string>(headers.Count);

        for (int j = 0; j < headers.Count; j++)
        {
            var value = j < values.Count ? values[j] : string.Empty;
            pairs.Add($"{headers[j]}: {value}");
        }

        return string.Join(" | ", pairs);
    }

    private static List<string> GetRowValues(Row row, SharedStringTable? sharedStrings)
    {
        var values = new List<string>();
        foreach (var cell in row.Elements<Cell>())
        {
            values.Add(GetCellValue(cell, sharedStrings));
        }
        return values;
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.CellValue is null)
        {
            if (cell.InlineString?.Text is not null)
            {
                return cell.InlineString.Text.Text;
            }
            return string.Empty;
        }

        var value = cell.CellValue.Text;

        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null)
        {
            return sharedStrings.ElementAt(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).InnerText;
        }

        return value;
    }
}
