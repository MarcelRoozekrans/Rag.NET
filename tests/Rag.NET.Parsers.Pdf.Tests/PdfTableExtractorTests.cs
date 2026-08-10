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
            prose.Select(w => w.Text).ToList(), StringComparer.Ordinal);
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
            prose.Select(w => w.Text).ToList(), StringComparer.Ordinal);
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
            AverageWordsPerCell = 1,
        };

        var markdown = PdfTableExtractor.RenderMarkdown(table);

        Assert.Equal("| Name | Age |\n| --- | --- |\n| A\\|B | 30 |", markdown);
    }

    [Fact]
    public void NarrowAlignedGaps_NotColumns()
    {
        // Pins the minimum gutter width (median height 10 * 1.5 = 15): a grid whose
        // aligned word-free intervals are only 12pt wide — ordinary word spacing — must
        // not be detected as columns, while the 60pt gutters in the tests above are.
        List<WordBox> words =
        [
            W("a", 0, 0), W("b", 52, 0), W("c", 104, 0),
            W("d", 0, 20), W("e", 52, 20), W("f", 104, 20),
            W("g", 0, 40), W("h", 52, 40), W("i", 104, 40),
        ];

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(9, prose.Count);
    }

    [Fact]
    public void RaggedSingleColumnProse_TwentyLines_NoTables()
    {
        // Reviewer probe (a): 20 lines of ragged single-column prose with deterministic
        // ("seeded") spacing. Inter-word gaps are 4-8pt (< the 15pt minimum gutter width),
        // every 3-row window contains a full-width line that kills trailing gaps, and
        // windows with two short lines fail the ragged tolerance — zero tables.
        double[] widths = [38, 55, 47, 62, 41, 50, 58, 44];
        double[] gaps = [4, 6, 5, 7, 8, 4, 6, 5];
        var words = new List<WordBox>();
        for (int line = 0; line < 20; line++)
        {
            int wordCount = line % 2 == 0 ? 8 : 5 + (line % 3);
            double x = 0;
            for (int i = 0; i < wordCount; i++)
            {
                double width = widths[(line + i) % widths.Length];
                words.Add(W($"w{line}_{i}", x, line * 20, width));
                x += width + gaps[(line + i) % gaps.Length];
            }
        }

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(words.Count, prose.Count);
    }

    [Fact]
    public void TwoColumnPageLayout_WideCenterGutter_NoTables()
    {
        // Reviewer probe (b): a 20-line two-column page layout with a persistent ~50pt
        // center gutter (well above the minimum gutter width). The plausibility guards
        // reject it: half-lines average 5 words per "cell" (> 4), and a 2-column run
        // spanning all of the page's rows is a layout, not a table.
        double[] widths = [36, 42, 38, 46, 40];
        double[] gaps = [5, 7, 6, 4, 8];
        var words = new List<WordBox>();
        for (int line = 0; line < 20; line++)
        {
            AddHalfLine(words, line, startX: 0, widths, gaps);
            AddHalfLine(words, line, startX: 280, widths, gaps);
        }

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(words.Count, prose.Count);
    }

    private static void AddHalfLine(
        List<WordBox> words, int line, double startX, double[] widths, double[] gaps)
    {
        double x = startX;
        for (int i = 0; i < 5; i++)
        {
            double width = widths[(line + i) % widths.Length];
            words.Add(W($"w{line}_{startX}_{i}", x, line * 20, width));
            x += width + gaps[(line + i) % gaps.Length];
        }
    }

    [Fact]
    public void ThreeColumnPageLayout_NewsletterStyle_NoTables()
    {
        // Reviewer probe (c): a 20-line newsletter-style THREE-column layout (~165pt
        // columns at x = 0/190/380, 3 words per column-line, cyclic seeded spacing).
        // At 3 words per "cell" this slips under the words-per-cell guard, so the
        // layout-dominance guard must reject it: a <= 3-column maximal run of 8+ rows
        // spanning more than half the page's rows is a page layout — the WHOLE run stays
        // prose (a per-window cap would still emit a sub-run as a false table).
        double[] widths = [44, 52, 47, 55, 41];
        double[] gaps = [5, 7, 4, 6, 8];
        var words = new List<WordBox>();
        for (int line = 0; line < 20; line++)
        {
            AddThirdLine(words, line, startX: 0, widths, gaps);
            AddThirdLine(words, line, startX: 190, widths, gaps);
            AddThirdLine(words, line, startX: 380, widths, gaps);
        }

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(words.Count, prose.Count);
    }

    private static void AddThirdLine(
        List<WordBox> words, int line, double startX, double[] widths, double[] gaps)
    {
        double x = startX;
        for (int i = 0; i < 3; i++)
        {
            double width = widths[(line + i) % widths.Length];
            words.Add(W($"w{line}_{startX}_{i}", x, line * 20, width));
            x += width + gaps[(line + i) % gaps.Length];
        }
    }

    // A 14-row Key/Value table: a two-word label and a one-word value per row.
    private static readonly (string KeyA, string KeyB, string Value)[] KeyValueRows =
    [
        ("Invoice", "Number", "INV-1001"),
        ("Order", "Date", "2026-01-04"),
        ("Due", "Date", "2026-02-03"),
        ("Customer", "Name", "Contoso"),
        ("Billing", "Country", "NL"),
        ("Shipping", "Method", "Express"),
        ("Payment", "Terms", "Net30"),
        ("Currency", "Code", "EUR"),
        ("Tax", "Rate", "21"),
        ("Discount", "Code", "SPRING26"),
        ("Sales", "Rep", "A.Jansen"),
        ("Purchase", "Order", "PO-88421"),
        ("Contract", "Id", "CT-7734"),
        ("Renewal", "Date", "2027-01-04"),
    ];

    [Fact]
    public void FullPageKeyValueTable_IsExtracted()
    {
        // The rescue case for the dense-cell exemption. A 2-column Key/Value table filling
        // the whole page: label words at x = 0 and x = 39/43 (alternating 38/42pt widths,
        // 5pt inter-word gap, right edge a constant 85pt), value word at x = 300.
        // The 5pt intra-label gap is below the 15pt minimum gutter width and is filtered;
        // the (85, 300) center gutter persists across every row → exactly 2 columns.
        // Value widths cycle 50/46/54 so the widest row leaves no trailing gap to intersect.
        //
        // 3 words / 2 non-empty cells = 1.50 words per cell — at or under the 2.0 exemption.
        // Without the exemption the layout-dominance guard rejects this run outright:
        // 2 columns (<= 3), 14 rows (>= 8), covering 14/14 of the page's rows (> 0.5).
        double[] keyWidths = [38, 42];
        double[] valueWidths = [50, 46, 54];
        var words = new List<WordBox>();
        for (int line = 0; line < KeyValueRows.Length; line++)
        {
            var row = KeyValueRows[line];
            double firstWidth = keyWidths[line % keyWidths.Length];
            words.Add(W(row.KeyA, 0, line * 20, firstWidth));
            words.Add(W(row.KeyB, firstWidth + 5, line * 20, keyWidths[(line + 1) % keyWidths.Length]));
            words.Add(W(row.Value, 300, line * 20, valueWidths[line % valueWidths.Length]));
        }

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        var table = Assert.Single(tables);
        Assert.Empty(prose);
        Assert.Equal(KeyValueRows.Length, table.Rows.Count);
        Assert.Equal(1.5, table.AverageWordsPerCell);
        Assert.Equal(new[] { "Invoice Number", "INV-1001" }, table.Rows[0]);
        Assert.Equal(new[] { "Tax Rate", "21" }, table.Rows[8]);
        Assert.Equal(new[] { "Renewal Date", "2027-01-04" }, table.Rows[13]);
    }

    [Fact]
    public void TwoColumnPageLayout_TwoAndAHalfWordsPerCell_NoTables()
    {
        // Pins the dense-cell exemption tight from the other side. Same full-page 2-column
        // shape as the Key/Value fixture (persistent 146pt center gutter at (134, 280)),
        // but the left column carries 3 words and the right column 2:
        // 5 words / 2 non-empty cells = 2.50 words per cell. That clears the 4.0
        // words-per-cell guard, so the run reaches the layout-dominance guard — and 2.50
        // is above the 2.0 exemption, so it is still rejected as a page layout.
        // Loosening the exemption to 3.0 would turn this red (and the newsletter test too).
        double[] leftWidths = [40, 44, 38];
        double[] rightWidths = [46, 50];
        var words = new List<WordBox>();
        for (int line = 0; line < 20; line++)
        {
            AddDenseProseColumn(words, line, startX: 0, wordCount: 3, leftWidths);
            AddDenseProseColumn(words, line, startX: 280, wordCount: 2, rightWidths);
        }

        var (tables, prose) = PdfTableExtractor.Extract(words, new PdfParserOptions());

        Assert.Empty(tables);
        Assert.Equal(words.Count, prose.Count);
    }

    private static void AddDenseProseColumn(
        List<WordBox> words, int line, double startX, int wordCount, double[] widths)
    {
        double x = startX;
        for (int i = 0; i < wordCount; i++)
        {
            double width = widths[(line + i) % widths.Length];
            words.Add(W($"w{line}_{startX}_{i}", x, line * 20, width));
            x += width + 6;
        }
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
