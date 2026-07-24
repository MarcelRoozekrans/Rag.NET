namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// Pure windowing/stitching math for over-long inputs: cuts TOKEN INDEX windows sized to the
/// model's input limit and stitches the per-window embedding matrices back into one matrix
/// covering every token. No ONNX dependency.
/// <para>
/// Sibling note: <c>Rag.NET.Chunking</c>'s internal <c>TokenWindowSplitter</c> looks similar
/// but does a different job — it windows CHAR OFFSETS of tokens to produce chunk text spans,
/// while this type windows token indices for model passes and merges embedding rows. Do not
/// merge them.
/// </para>
/// </summary>
internal static class TokenWindowStitcher
{
    /// <summary>
    /// Cuts <c>[Start, End)</c> token-index windows covering <c>0..tokenCount</c> with step
    /// <c>maxTokens - overlap</c>. The last window may be shorter; a single window is returned
    /// when <paramref name="tokenCount"/> fits in <paramref name="maxTokens"/>; zero windows
    /// when <paramref name="tokenCount"/> is zero or negative.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// When <paramref name="overlap"/> is negative or not smaller than
    /// <paramref name="maxTokens"/> (which also rejects non-positive
    /// <paramref name="maxTokens"/>).
    /// </exception>
    public static List<(int Start, int End)> Windows(int tokenCount, int maxTokens, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTokens, overlap);

        var windows = new List<(int Start, int End)>();
        if (tokenCount <= 0)
            return windows;

        var step = maxTokens - overlap;
        for (var start = 0; ; start += step)
        {
            var end = Math.Min(start + maxTokens, tokenCount);
            windows.Add((start, end));
            if (end == tokenCount)
                break;
        }

        return windows;
    }

    /// <summary>
    /// Merges per-window row-major matrices into one row-major
    /// <c>float[totalTokens * dimension]</c> matrix. Windows are copied in order, so rows in
    /// overlap regions are overwritten by the LATER window: its vectors were computed with the
    /// overlap tokens sitting near the start of a fresh window and therefore carry more
    /// right-context (the earlier window saw them at its right edge with no context after
    /// them).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When the matrix and window counts differ, a matrix's length does not match its
    /// window's <c>(End - Start) * dimension</c>, or the windows do not cover
    /// <c>0..totalTokens</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// When <paramref name="dimension"/> is not positive.
    /// </exception>
    public static float[] Stitch(
        IReadOnlyList<float[]> windowMatrices,
        IReadOnlyList<(int Start, int End)> windows,
        int dimension,
        int totalTokens)
    {
        ArgumentNullException.ThrowIfNull(windowMatrices);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

        if (windowMatrices.Count != windows.Count)
        {
            throw new ArgumentException(
                $"Matrix count ({windowMatrices.Count}) does not match window count ({windows.Count}).",
                nameof(windowMatrices));
        }

        ValidateCoverage(windows, totalTokens);

        var stitched = new float[totalTokens * dimension];
        for (var w = 0; w < windows.Count; w++)
        {
            var (start, end) = windows[w];
            var matrix = windowMatrices[w];
            var expected = (end - start) * dimension;
            if (matrix.Length != expected)
            {
                throw new ArgumentException(
                    $"Matrix {w} has length {matrix.Length} but window ({start}, {end}) with dimension {dimension} requires {expected}.",
                    nameof(windowMatrices));
            }

            // Later windows intentionally overwrite the overlap rows of earlier ones.
            Array.Copy(matrix, 0, stitched, start * dimension, expected);
        }

        return stitched;
    }

    /// <summary>
    /// Windows must start at token 0, advance contiguously or overlapping (no gaps), and end
    /// exactly at <paramref name="totalTokens"/> — otherwise some stitched rows would silently
    /// stay zero-filled.
    /// </summary>
    private static void ValidateCoverage(IReadOnlyList<(int Start, int End)> windows, int totalTokens)
    {
        if (totalTokens == 0 && windows.Count == 0)
            return;

        if (windows.Count == 0 || windows[0].Start != 0 || windows[^1].End != totalTokens)
        {
            throw new ArgumentException(
                $"Windows must cover tokens 0..{totalTokens} (got {windows.Count} window(s), " +
                $"first start {(windows.Count > 0 ? windows[0].Start : 0)}, last end {(windows.Count > 0 ? windows[^1].End : 0)}).",
                nameof(windows));
        }

        for (var w = 1; w < windows.Count; w++)
        {
            if (windows[w].Start > windows[w - 1].End)
            {
                throw new ArgumentException(
                    $"Gap between window {w - 1} (end {windows[w - 1].End}) and window {w} (start {windows[w].Start}).",
                    nameof(windows));
            }
        }
    }
}
