using Rag.NET.Parsers.Pdf.TableExtraction;
using Xunit;

namespace Rag.NET.Parsers.Pdf.Tests;

/// <summary>
/// Pure-geometry tests for <see cref="PdfTableExtractor"/> on synthetic, hand-computed
/// <see cref="WordBox"/> data (top-down Y coordinates, no PDF involved).
/// </summary>
public class PdfTableExtractorTests
{
    private static WordBox W(string text, double x, double y, double width = 40, double height = 10) =>
        new(text, x, y, width, height);

    // 3x3 grid: columns at X = 0/100/200 (width 40 → gutters (40,100) and (140,200)),
    // rows at Y = 0/20/40. All heights 10 → median 10 → row band tolerance 10 * 0.6 = 6.
    private static List<WordBox> ThreeByThreeGrid() =>
    [
        W("Name", 0, 0), W("Age", 100, 5), W("City", 200, 3),
        W("Alice", 0, 20), W("30", 100, 24), W("Paris", 200, 22),
        W("Bob", 0, 40), W("25", 100, 41), W("London", 200, 45),
    ];

    [Fact]
    public void ThreeByThreeGrid_DetectedAsOneTable()
    {
        // Y jitter of 5 within each row pins the tolerance constant (median height 10 * 0.6 = 6):
        // a smaller factor (e.g. 0.4 → tolerance 4) would split the jittered rows apart.
        var (tables, prose) = PdfTableExtractor.Extract(ThreeByThreeGrid(), new PdfParserOptions());

        var table = Assert.Single(tables);
        Assert.Empty(prose);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "Name", "Age", "City" }, table.Rows[0]);
        Assert.Equal(new[] { "Alice", "30", "Paris" }, table.Rows[1]);
        Assert.Equal(new[] { "Bob", "25", "London" }, table.Rows[2]);
        Assert.Equal(0, table.TopY);
        Assert.Equal(55, table.BottomY); // lowest word: Y=45 + height 10
    }

    [Fact]
    public void ProseParagraph_NoTable()
    {
        // Three text lines whose inter-word gaps never align vertically:
        // r1 gaps (50,60),(120,130); r2 gaps (55,65),(125,135); r3 gaps (40,45),(115,125).
        // r1∩r2 = (55,60),(125,130); ∩ r3 = empty → no persistent column gutters.
        List<WordBox> words =
        [
            W("aa", 0, 0, 50), W("bb", 60, 0, 60), W("cc", 130, 0, 70),
            W("dd", 0, 20, 55), W("ee", 65, 20, 60), W("ff", 135, 20, 65),
            W("gg", 0, 40, 40), W("hh", 45, 40, 70), W("ii", 125, 40, 75),
        ];

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(9, prose.Count);
        // Prose words come back in reading order: Y band, then X.
        Assert.Equal(
            new[] { "aa", "bb", "cc", "dd", "ee", "ff", "gg", "hh", "ii" },
            prose.Select(w => w.Text).ToList());
    }

    [Fact]
    public void TwoColumnsOnly_MinColumnsRespected()
    {
        // A clean 3x2 grid, but MinTableColumns = 3 → only one persistent gutter → no table.
        List<WordBox> words =
        [
            W("a", 0, 0), W("b", 100, 0),
            W("c", 0, 20), W("d", 100, 20),
            W("e", 0, 40), W("f", 100, 40),
        ];

        var (tables, prose) = PdfTableExtractor.Extract(
            words, new PdfParserOptions { MinTableColumns = 3 });

        Assert.Empty(tables);
        Assert.Equal(6, prose.Count);
    }

    [Fact]
    public void TwoRowsOnly_MinRowsRespected()
    {
        // A clean 2x3 grid: fewer rows than MinTableRows (default 3) → no table.
        List<WordBox> words =
        [
            W("a", 0, 0), W("b", 100, 0), W("c", 200, 0),
            W("d", 0, 20), W("e", 100, 20), W("f", 200, 20),
        ];

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(6, prose.Count);
    }

    [Fact]
    public void RaggedHeaderRow_Tolerated()
    {
        // Header occupies only columns 1-2 of a 3-column body (misses one column).
        // Run extent right edge = 240; header gaps (40,100),(140,240) intersect the body
        // gutters (40,100),(140,200) → boundaries survive; header gets one empty cell.
        List<WordBox> words =
        [
            W("Name", 0, 0), W("Age", 100, 0),
            W("A1", 0, 20), W("B1", 100, 20), W("C1", 200, 20),
            W("A2", 0, 40), W("B2", 100, 40), W("C2", 200, 40),
            W("A3", 0, 60), W("B3", 100, 60), W("C3", 200, 60),
        ];

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        var table = Assert.Single(tables);
        Assert.Empty(prose);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(new[] { "Name", "Age", "" }, table.Rows[0]);
        Assert.Equal(new[] { "A1", "B1", "C1" }, table.Rows[1]);
        Assert.Equal(new[] { "A3", "B3", "C3" }, table.Rows[3]);
    }

    [Fact]
    public void MixedPageProseAboveTableBelow_SplitCorrectly()
    {
        // Two prose lines above the 3x3 grid. Each prose line fully covers both grid
        // gutters ((40,100) and (140,200)) with words, and the two lines' own inter-word
        // gaps do not align with each other, so no window containing a prose row finds
        // enough persistent gutters. (A sparse prose line whose whitespace happens to
        // align with the gutters is a documented false-positive of the heuristic.)
        List<WordBox> words =
        [
            W("p1", 0, 0, 30), W("p2", 35, 0, 85), W("p3", 125, 0, 90), W("p4", 220, 0, 20),
            W("q1", 0, 20, 20), W("q2", 25, 20, 85), W("q3", 115, 20, 95), W("q4", 215, 20, 25),
        ];
        words.AddRange(
        [
            W("Name", 0, 60), W("Age", 100, 60), W("City", 200, 60),
            W("Alice", 0, 80), W("30", 100, 80), W("Paris", 200, 80),
            W("Bob", 0, 100), W("25", 100, 100), W("London", 200, 100),
        ]);

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        var table = Assert.Single(tables);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "Name", "Age", "City" }, table.Rows[0]);
        Assert.Equal(60, table.TopY);
        Assert.Equal(
            new[] { "p1", "p2", "p3", "p4", "q1", "q2", "q3", "q4" },
            prose.Select(w => w.Text).ToList());
    }

    [Fact]
    public void RenderMarkdown_PipeFormat()
    {
        var table = new DetectedTable
        {
            Rows =
            [
                ["Name", "Age"],
                ["A|B", "30"],
            ],
            TopY = 0,
            BottomY = 30,
        };

        var markdown = PdfTableExtractor.RenderMarkdown(table);

        Assert.Equal("| Name | Age |\n| --- | --- |\n| A\\|B | 30 |", markdown);
    }

    [Fact]
    public void InconsistentColumns_BailsToProse()
    {
        // Rows disagree on column count: two-word rows (cols 1+3) dominate around a single
        // three-word row. Every candidate window resolves to 3 columns but has >= 2 rows
        // with an empty middle cell — beyond the single-ragged-row tolerance → no table.
        List<WordBox> words =
        [
            W("a1", 0, 0), W("c1", 200, 0),
            W("a2", 0, 20), W("c2", 200, 20),
            W("a3", 0, 40), W("b3", 100, 40), W("c3", 200, 40),
            W("a4", 0, 60), W("c4", 200, 60),
            W("a5", 0, 80), W("c5", 200, 80),
        ];

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(11, prose.Count);
    }
}
