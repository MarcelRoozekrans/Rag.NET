using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Generator for the checked-in fixture
/// <c>tests/Rag.NET.Parsers.IntegrationTests/Resources/sample-table.pdf</c> (linked into this
/// project as an embedded resource). The fixture was produced once by writing
/// <see cref="Generate"/>'s output to disk (PdfPig 0.1.15 <c>PdfDocumentBuilder</c>);
/// rerun it and overwrite the resource to regenerate.
///
/// Layout (PDF bottom-up coordinates, Letter portrait 612x792, Helvetica 12pt):
/// - "Introduction" at (100, 700) — prose line above the table.
/// - 3x3 table at columns x = 100/300/500, rows y = 600/560/520:
///   Name/Age/City, Alice/30/Paris, Bob/25/London.
/// - "Conclusion" at (100, 420) — prose line below the table.
/// </summary>
internal static class TableFixtureGenerator
{
    internal static byte[] Generate()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.Letter, isPortrait: true);

        page.AddText("Introduction", 12, new PdfPoint(100, 700), font);

        string[][] cells =
        [
            ["Name", "Age", "City"],
            ["Alice", "30", "Paris"],
            ["Bob", "25", "London"],
        ];
        double[] columnX = [100, 300, 500];
        double[] rowY = [600, 560, 520];
        for (int r = 0; r < cells.Length; r++)
        {
            for (int c = 0; c < columnX.Length; c++)
            {
                page.AddText(cells[r][c], 12, new PdfPoint(columnX[c], rowY[r]), font);
            }
        }

        page.AddText("Conclusion", 12, new PdfPoint(100, 420), font);

        return builder.Build();
    }
}
