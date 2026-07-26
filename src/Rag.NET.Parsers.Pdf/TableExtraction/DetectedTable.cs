namespace Rag.NET.Parsers.Pdf.TableExtraction;

/// <summary>A table detected from word geometry: rows of cell strings plus the source Y-range.</summary>
internal sealed class DetectedTable
{
    /// <summary>Cell text per row; every row has the same column count (empty string = empty cell).</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    /// <summary>Top edge (top-down page coordinates) of the highest word in the table.</summary>
    public required double TopY { get; init; }

    /// <summary>Bottom edge (top-down page coordinates) of the lowest word in the table.</summary>
    public required double BottomY { get; init; }

    /// <summary>
    /// Average number of source words per non-empty cell across the run, as computed by the
    /// plausibility guard that already had the row data. It rides on the table because the
    /// layout-dominance guard runs after the run has been extended to maximal, at a call site
    /// that no longer has access to the rows and would otherwise have to recompute it.
    /// </summary>
    public required double AverageWordsPerCell { get; init; }
}
