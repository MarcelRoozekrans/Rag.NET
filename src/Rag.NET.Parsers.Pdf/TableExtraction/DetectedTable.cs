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
}
