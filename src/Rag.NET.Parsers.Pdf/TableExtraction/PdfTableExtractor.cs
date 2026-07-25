using System.Text;

namespace Rag.NET.Parsers.Pdf.TableExtraction;

/// <summary>
/// Pure-geometry table detection over word boxes (no PdfPig types). Algorithm:
/// words are clustered into rows by Y bands (tolerance = median word height *
/// <see cref="RowBandToleranceFactor"/>); column gutters are X-intervals that stay word-free
/// across every row of a candidate run of >= <see cref="PdfParserOptions.MinTableRows"/>
/// vertically adjacent rows; a maximal such run is one table. One row per run may leave at
/// most one column empty (ragged header tolerance); anything less consistent bails to prose.
/// </summary>
internal static class PdfTableExtractor
{
    /// <summary>
    /// Row-banding tolerance factor: a word joins the current Y band when its top is within
    /// (median word height * this factor) of the band's first word top.
    /// </summary>
    private const double RowBandToleranceFactor = 0.6;

    internal static (IReadOnlyList<DetectedTable> Tables, IReadOnlyList<WordBox> ProseWords) Extract(
        IReadOnlyList<WordBox> words, PdfParserOptions options)
    {
        if (words.Count == 0)
        {
            return ([], []);
        }

        var rows = ClusterRows(words);
        var (tables, consumed) = DetectTables(rows, options);
        var prose = CollectWords(rows, consumed);
        return (tables, prose);
    }

    /// <summary>Renders a detected table as a Markdown pipe table (header separator after row 1).</summary>
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

    private static List<List<WordBox>> ClusterRows(IReadOnlyList<WordBox> words)
    {
        var sorted = new List<WordBox>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            sorted.Add(words[i]);
        }

        sorted.Sort(static (a, b) => a.Y.CompareTo(b.Y));

        double tolerance = MedianHeight(sorted) * RowBandToleranceFactor;
        var rows = new List<List<WordBox>>();
        List<WordBox>? current = null;
        double bandTop = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            var word = sorted[i];
            if (current is null || word.Y - bandTop > tolerance)
            {
                current = [];
                rows.Add(current);
                bandTop = word.Y;
            }

            current.Add(word);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Sort(static (a, b) => a.X.CompareTo(b.X));
        }

        return rows;
    }

    private static double MedianHeight(List<WordBox> words)
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
        List<List<WordBox>> rows, PdfParserOptions options)
    {
        var tables = new List<DetectedTable>();
        var consumed = new bool[rows.Count];
        int start = 0;
        while (start + options.MinTableRows <= rows.Count)
        {
            int end = start + options.MinTableRows - 1;
            var table = TryBuildTable(rows, start, end, options);
            if (table is null)
            {
                start++;
                continue;
            }

            // Maximal run: extend downward while the enlarged window still forms a table.
            while (end + 1 < rows.Count)
            {
                var extended = TryBuildTable(rows, start, end + 1, options);
                if (extended is null)
                {
                    break;
                }

                table = extended;
                end++;
            }

            tables.Add(table);
            Array.Fill(consumed, true, start, end - start + 1);
            start = end + 1;
        }

        return (tables, consumed);
    }

    private static DetectedTable? TryBuildTable(
        List<List<WordBox>> rows, int start, int end, PdfParserOptions options)
    {
        var (left, right) = ComputeExtent(rows, start, end);
        var persistent = ComputeRowGaps(rows[start], left, right);
        for (int r = start + 1; r <= end && persistent.Count > 0; r++)
        {
            persistent = IntersectGaps(persistent, ComputeRowGaps(rows[r], left, right));
        }

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

        var (top, bottom) = ComputeYRange(rows, start, end);
        return new DetectedTable { Rows = cells, TopY = top, BottomY = bottom };
    }

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
