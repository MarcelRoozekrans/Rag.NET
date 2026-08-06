using System.Text;

namespace Rag.NET.Parsers.Pdf.TableExtraction;

/// <summary>
/// Pure-geometry table detection over word boxes (no PdfPig types). Algorithm:
/// words are clustered into rows by baseline (bottom-edge) Y bands (tolerance = median word
/// height * <see cref="RowBandToleranceFactor"/>); column gutters are X-intervals at least
/// <see cref="MinGutterWidthFactor"/> * median word height wide that stay word-free across
/// every row of a candidate run of >= <see cref="PdfParserOptions.MinTableRows"/> vertically
/// adjacent rows; a maximal such run is one table. One row per run may leave at most one
/// column empty (ragged header tolerance); anything less consistent, or a run that looks
/// like multi-column prose (see <see cref="PassesPlausibilityGuards"/>), bails to prose.
///
/// Known limitations (documented for the parser guide) — the guards prefer conservative
/// false negatives (prose, today's behavior) over false-positive tables:
/// - The first detected row is assumed to be the header when rendering Markdown.
/// - Extraction is per page: a table spanning a page break is emitted as two tables.
/// - Column gutters narrower than 1.5x the median word height are not detected
///   (very tight tables degrade to prose).
/// - Cells averaging more than 4 words degrade to prose (e.g. long description columns).
/// - A 2- or 3-column run of 8+ rows spanning more than half the page's rows is treated
///   as a multi-column page layout and stays prose, UNLESS its cells average 2 words or
///   fewer (dense Key/Value content). Whole-page 2/3-column tables whose cells run longer
///   than that are still missed by design.
/// </summary>
internal static class PdfTableExtractor
{
    /// <summary>
    /// Row-banding tolerance factor: a word joins the current Y band when its baseline
    /// (bottom edge) is within (median word height * this factor) of the band's first
    /// word baseline.
    /// </summary>
    private const double RowBandToleranceFactor = 0.6;

    /// <summary>
    /// Minimum column-gutter width as a factor of the median word height: an intersected
    /// word-free interval only counts as a column gutter when at least this wide. Ordinary
    /// inter-word spaces (roughly 0.3-0.8x the word height) that happen to align across
    /// adjacent prose lines must not become columns.
    /// </summary>
    private const double MinGutterWidthFactor = 1.5;

    /// <summary>
    /// Prose-layout guard: bail when the average number of words per non-empty cell exceeds
    /// this value. Genuine table cells are short (typically 1-3 words); half-lines of
    /// multi-column prose run longer.
    /// </summary>
    private const double MaxAverageWordsPerCell = 4.0;

    /// <summary>
    /// Layout-dominance guard: a maximal run with at most <see cref="MaxLayoutColumns"/>
    /// columns, at least <see cref="MinLayoutRunRows"/> rows, and spanning more than this
    /// fraction of the page's rows is a multi-column page layout (academic two-column,
    /// newsletter three-column), not a table — the whole run stays prose.
    /// </summary>
    private const double LayoutDominanceFraction = 0.5;

    /// <summary>Column-count ceiling for the layout-dominance guard (2-3 column page layouts).</summary>
    private const int MaxLayoutColumns = 3;

    /// <summary>
    /// Row floor for the layout-dominance guard: shorter runs are plausible tables even when
    /// they span most of a page's rows (a small grid on an otherwise sparse page).
    /// </summary>
    private const int MinLayoutRunRows = 8;

    /// <summary>
    /// Dense-content exemption from the layout-dominance guard: a run averaging at most this
    /// many words per non-empty cell is Key/Value-style tabular content (labels and short
    /// values), not prose columns, and stays a table even when it dominates the page.
    ///
    /// The value is tight by necessity and is not a free parameter. Two fixtures pin it from
    /// above: newsletter-style three-column prose sits at exactly 3.0 words per cell, and a
    /// full-page two-column layout fixture sits at 2.50 — the latter is the binding one, so
    /// anything above ~2.5 reclassifies a page layout as a table. It is strictly nested inside
    /// <see cref="MaxAverageWordsPerCell"/> — a run must already have passed that (4.0) to
    /// reach this check at all.
    /// </summary>
    private const double DenseCellExemptionWordsPerCell = 2.0;

    internal static (IReadOnlyList<DetectedTable> Tables, IReadOnlyList<WordBox> ProseWords) Extract(
        IReadOnlyList<WordBox> words, PdfParserOptions options)
    {
        if (words.Count == 0)
        {
            return ([], []);
        }

        double medianHeight = MedianHeight(words);
        var rows = ClusterRows(words, medianHeight * RowBandToleranceFactor);
        var (tables, consumed) = DetectTables(rows, options, medianHeight * MinGutterWidthFactor);
        var prose = CollectWords(rows, consumed);
        return (tables, prose);
    }

    /// <summary>
    /// Renders a detected table as a Markdown pipe table. The first detected row is assumed
    /// to be the header row (separator emitted after row 1) — a heuristic assumption.
    /// </summary>
    internal static string RenderMarkdown(DetectedTable table)
    {
        var builder = new StringBuilder();
        AppendMarkdownRow(builder, table.Rows[0]);
        builder.Append('\n');
        AppendMarkdownSeparator(builder, table.Rows[0].Count);
        for (int r = 1; r < table.Rows.Count; r++)
        {
            builder.Append('\n');
            AppendMarkdownRow(builder, table.Rows[r]);
        }

        return builder.ToString();
    }

    // ── Row clustering ───────────────────────────────────────────────────────

    private static List<List<WordBox>> ClusterRows(IReadOnlyList<WordBox> words, double tolerance)
    {
        var sorted = new List<WordBox>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            sorted.Add(words[i]);
        }

        // Band on the bottom edge (the baseline): words sharing a visual line align on
        // their baseline even when font sizes differ, whereas their top edges diverge.
        sorted.Sort(static (a, b) => (a.Y + a.Height).CompareTo(b.Y + b.Height));

        var rows = new List<List<WordBox>>();
        List<WordBox>? current = null;
        double bandBaseline = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            var word = sorted[i];
            double baseline = word.Y + word.Height;
            if (current is null || baseline - bandBaseline > tolerance)
            {
                current = [];
                rows.Add(current);
                bandBaseline = baseline;
            }

            current.Add(word);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Sort(static (a, b) => a.X.CompareTo(b.X));
        }

        return rows;
    }

    private static double MedianHeight(IReadOnlyList<WordBox> words)
    {
        var heights = new double[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            heights[i] = words[i].Height;
        }

        Array.Sort(heights);
        int mid = heights.Length / 2;
        return heights.Length % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2;
    }

    // ── Run detection ────────────────────────────────────────────────────────

    private static (List<DetectedTable> Tables, bool[] Consumed) DetectTables(
        List<List<WordBox>> rows, PdfParserOptions options, double minGutterWidth)
    {
        var tables = new List<DetectedTable>();
        var consumed = new bool[rows.Count];
        int start = 0;
        while (start + options.MinTableRows <= rows.Count)
        {
            int end = start + options.MinTableRows - 1;
            var table = TryBuildTable(rows, start, end, options, minGutterWidth);
            if (table is null)
            {
                start++;
                continue;
            }

            // Maximal run: extend downward while the enlarged window still forms a table.
            while (end + 1 < rows.Count)
            {
                var extended = TryBuildTable(rows, start, end + 1, options, minGutterWidth);
                if (extended is null)
                {
                    break;
                }

                table = extended;
                end++;
            }

            if (IsLayoutDominated(table, end - start + 1, rows.Count))
            {
                // Skip the entire run unconsumed (its sub-windows are equally layout prose).
                start = end + 1;
                continue;
            }

            tables.Add(table);
            Array.Fill(consumed, true, start, end - start + 1);
            start = end + 1;
        }

        return (tables, consumed);
    }

    private static DetectedTable? TryBuildTable(
        List<List<WordBox>> rows, int start, int end, PdfParserOptions options, double minGutterWidth)
    {
        var (left, right) = ComputeExtent(rows, start, end);
        var persistent = ComputeRowGaps(rows[start], left, right);
        for (int r = start + 1; r <= end && persistent.Count > 0; r++)
        {
            persistent = IntersectGaps(persistent, ComputeRowGaps(rows[r], left, right));
        }

        persistent = FilterNarrowGutters(persistent, minGutterWidth);
        if (persistent.Count < options.MinTableColumns - 1)
        {
            return null;
        }

        var boundaries = new double[persistent.Count];
        for (int i = 0; i < persistent.Count; i++)
        {
            boundaries[i] = (persistent[i].Start + persistent[i].End) / 2;
        }

        var cells = BuildCells(rows, start, end, boundaries);
        if (!HasConsistentColumns(cells))
        {
            return null;
        }

        if (!PassesPlausibilityGuards(rows, start, end, cells, out double averageWordsPerCell))
        {
            return null;
        }

        var (top, bottom) = ComputeYRange(rows, start, end);
        return new DetectedTable
        {
            Rows = cells,
            TopY = top,
            BottomY = bottom,
            AverageWordsPerCell = averageWordsPerCell,
        };
    }

    /// <summary>
    /// Drops intersected word-free intervals narrower than the minimum gutter width:
    /// ordinary inter-word spaces aligning across a few lines are not column gutters.
    /// </summary>
    private static List<(double Start, double End)> FilterNarrowGutters(
        List<(double Start, double End)> gaps, double minGutterWidth)
    {
        var result = new List<(double Start, double End)>(gaps.Count);
        for (int i = 0; i < gaps.Count; i++)
        {
            if (gaps[i].End - gaps[i].Start >= minGutterWidth)
            {
                result.Add(gaps[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Per-window guard against multi-column prose masquerading as a table: genuine table
    /// cells are short — bail when the average words per non-empty cell exceeds
    /// <see cref="MaxAverageWordsPerCell"/> (prose half-lines run longer). The complementary
    /// layout-dominance guard runs post-extension in <see cref="DetectTables"/> and reuses
    /// the ratio computed here via <paramref name="averageWordsPerCell"/>, which is carried
    /// on the <see cref="DetectedTable"/>; that guard has no row data of its own.
    /// </summary>
    /// <param name="rows">All word rows on the page, indexed by <paramref name="start"/> and
    /// <paramref name="end"/>; only the window between them is inspected.</param>
    /// <param name="start">Index of the window's first row, inclusive.</param>
    /// <param name="end">Index of the window's last row, inclusive.</param>
    /// <param name="cells">Cell text already extracted for the window, one entry per row.
    /// Empty cells are excluded from the ratio, which is why an all-empty window yields 0.</param>
    /// <param name="averageWordsPerCell">
    /// Words per non-empty cell over the window, or 0 when the window has no non-empty cells —
    /// in which case the method returns false and no table is built, so a 0 ratio never
    /// reaches the dominance guard (where it would otherwise read as "exempt").
    /// </param>
    private static bool PassesPlausibilityGuards(
        List<List<WordBox>> rows,
        int start,
        int end,
        List<IReadOnlyList<string>> cells,
        out double averageWordsPerCell)
    {
        int totalWords = 0;
        for (int r = start; r <= end; r++)
        {
            totalWords += rows[r].Count;
        }

        int nonEmptyCells = 0;
        for (int r = 0; r < cells.Count; r++)
        {
            var row = cells[r];
            for (int c = 0; c < row.Count; c++)
            {
                if (row[c].Length > 0)
                {
                    nonEmptyCells++;
                }
            }
        }

        if (nonEmptyCells == 0)
        {
            averageWordsPerCell = 0;
            return false;
        }

        averageWordsPerCell = (double)totalWords / nonEmptyCells;
        return averageWordsPerCell <= MaxAverageWordsPerCell;
    }

    /// <summary>
    /// Layout-dominance guard, applied to the MAXIMAL run (not per window — a per-window
    /// check would merely cap the run and still emit a sub-run of the layout as a table):
    /// a run with at most <see cref="MaxLayoutColumns"/> columns that is at least
    /// <see cref="MinLayoutRunRows"/> rows long and spans more than
    /// <see cref="LayoutDominanceFraction"/> of the page's rows is a multi-column page
    /// layout; the whole run is skipped as prose. The row floor keeps small genuine tables
    /// (which may well be 100% of a sparse page's rows) detectable, and the
    /// <see cref="DenseCellExemptionWordsPerCell"/> conjunct keeps full-page Key/Value tables
    /// detectable: prose columns carry more words per cell than dense label/value pairs.
    /// </summary>
    private static bool IsLayoutDominated(DetectedTable table, int runRows, int totalRows) =>
        table.Rows[0].Count <= MaxLayoutColumns
        && runRows >= MinLayoutRunRows
        && runRows > totalRows * LayoutDominanceFraction
        && table.AverageWordsPerCell > DenseCellExemptionWordsPerCell;

    // ── Gap geometry ─────────────────────────────────────────────────────────

    /// <summary>Maximal word-free X-intervals of a row within the run extent [left, right].</summary>
    private static List<(double Start, double End)> ComputeRowGaps(
        List<WordBox> row, double left, double right)
    {
        var gaps = new List<(double Start, double End)>();
        double cursor = left;
        for (int i = 0; i < row.Count; i++)
        {
            if (row[i].X > cursor)
            {
                gaps.Add((cursor, row[i].X));
            }

            cursor = Math.Max(cursor, row[i].X + row[i].Width);
        }

        if (cursor < right)
        {
            gaps.Add((cursor, right));
        }

        return gaps;
    }

    private static List<(double Start, double End)> IntersectGaps(
        List<(double Start, double End)> first, List<(double Start, double End)> second)
    {
        var result = new List<(double Start, double End)>();
        int i = 0;
        int j = 0;
        while (i < first.Count && j < second.Count)
        {
            double start = Math.Max(first[i].Start, second[j].Start);
            double end = Math.Min(first[i].End, second[j].End);
            if (end > start)
            {
                result.Add((start, end));
            }

            if (first[i].End < second[j].End)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return result;
    }

    private static (double Left, double Right) ComputeExtent(
        List<List<WordBox>> rows, int start, int end)
    {
        double left = double.MaxValue;
        double right = double.MinValue;
        for (int r = start; r <= end; r++)
        {
            var row = rows[r];
            for (int i = 0; i < row.Count; i++)
            {
                left = Math.Min(left, row[i].X);
                right = Math.Max(right, row[i].X + row[i].Width);
            }
        }

        return (left, right);
    }

    private static (double Top, double Bottom) ComputeYRange(
        List<List<WordBox>> rows, int start, int end)
    {
        double top = double.MaxValue;
        double bottom = double.MinValue;
        for (int r = start; r <= end; r++)
        {
            var row = rows[r];
            for (int i = 0; i < row.Count; i++)
            {
                top = Math.Min(top, row[i].Y);
                bottom = Math.Max(bottom, row[i].Y + row[i].Height);
            }
        }

        return (top, bottom);
    }

    // ── Cell assembly + consistency ──────────────────────────────────────────

    private static List<IReadOnlyList<string>> BuildCells(
        List<List<WordBox>> rows, int start, int end, double[] boundaries)
    {
        int columnCount = boundaries.Length + 1;
        var cells = new List<IReadOnlyList<string>>(end - start + 1);
        for (int r = start; r <= end; r++)
        {
            var row = rows[r];
            var rowCells = new string[columnCount];
            Array.Fill(rowCells, string.Empty);
            for (int i = 0; i < row.Count; i++)
            {
                int column = ColumnOf(row[i], boundaries);
                rowCells[column] = rowCells[column].Length == 0
                    ? row[i].Text
                    : $"{rowCells[column]} {row[i].Text}";
            }

            cells.Add(rowCells);
        }

        return cells;
    }

    private static int ColumnOf(in WordBox word, double[] boundaries)
    {
        double center = word.X + (word.Width / 2);
        int column = 0;
        while (column < boundaries.Length && center > boundaries[column])
        {
            column++;
        }

        return column;
    }

    /// <summary>
    /// Column-count consistency: at most one row (the ragged header) may leave one column
    /// empty; a second sparse row, or any row missing two or more columns, bails the run.
    /// </summary>
    private static bool HasConsistentColumns(List<IReadOnlyList<string>> cells)
    {
        int raggedRows = 0;
        for (int r = 0; r < cells.Count; r++)
        {
            int empty = 0;
            var row = cells[r];
            for (int c = 0; c < row.Count; c++)
            {
                if (row[c].Length == 0)
                {
                    empty++;
                }
            }

            if (empty >= 2)
            {
                return false;
            }

            if (empty == 1)
            {
                raggedRows++;
            }
        }

        return raggedRows <= 1;
    }

    // ── Output assembly ──────────────────────────────────────────────────────

    private static List<WordBox> CollectWords(List<List<WordBox>> rows, bool[] consumed)
    {
        var words = new List<WordBox>();
        for (int r = 0; r < rows.Count; r++)
        {
            if (consumed[r])
            {
                continue;
            }

            var row = rows[r];
            for (int i = 0; i < row.Count; i++)
            {
                words.Add(row[i]);
            }
        }

        return words;
    }

    private static void AppendMarkdownRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        builder.Append('|');
        for (int c = 0; c < cells.Count; c++)
        {
            builder.Append(' ')
                .Append(cells[c].Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" |");
        }
    }

    private static void AppendMarkdownSeparator(StringBuilder builder, int columnCount)
    {
        builder.Append('|');
        for (int c = 0; c < columnCount; c++)
        {
            builder.Append(" --- |");
        }
    }
}
