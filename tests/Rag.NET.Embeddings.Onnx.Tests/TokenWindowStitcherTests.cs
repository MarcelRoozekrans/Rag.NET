using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

public class TokenWindowStitcherTests
{
    // ── Windows: window math ─────────────────────────────────────────────────

    [Fact]
    public void Windows_TokenCountBelowMax_SingleShortWindow()
    {
        var windows = TokenWindowStitcher.Windows(tokenCount: 5, maxTokens: 8, overlap: 2);

        var window = Assert.Single(windows);
        Assert.Equal((0, 5), window);
    }

    [Fact]
    public void Windows_TokenCountEqualsMax_SingleFullWindow()
    {
        var windows = TokenWindowStitcher.Windows(tokenCount: 8, maxTokens: 8, overlap: 2);

        var window = Assert.Single(windows);
        Assert.Equal((0, 8), window);
    }

    [Fact]
    public void Windows_TokenCountAboveMax_MultipleWindowsWithOverlap()
    {
        // step = 4 - 1 = 3 → (0,4), (3,7), (6,10)
        var windows = TokenWindowStitcher.Windows(tokenCount: 10, maxTokens: 4, overlap: 1);

        Assert.Equal([(0, 4), (3, 7), (6, 10)], windows);
    }

    [Fact]
    public void Windows_RemainderWindow_IsShort()
    {
        // step = 4 → (0,4), (4,8), (8,9): last window has a single token
        var windows = TokenWindowStitcher.Windows(tokenCount: 9, maxTokens: 4, overlap: 0);

        Assert.Equal([(0, 4), (4, 8), (8, 9)], windows);
    }

    [Fact]
    public void Windows_CoverEveryToken_WithoutGaps()
    {
        var windows = TokenWindowStitcher.Windows(tokenCount: 100, maxTokens: 16, overlap: 5);

        Assert.Equal(0, windows[0].Start);
        Assert.Equal(100, windows[^1].End);
        for (var i = 1; i < windows.Count; i++)
        {
            Assert.Equal(windows[i - 1].End - 5, windows[i].Start); // exact overlap
            Assert.True(windows[i].End > windows[i - 1].End, "each window must advance");
        }
    }

    [Fact]
    public void Windows_ZeroTokens_YieldNoWindows()
    {
        var windows = TokenWindowStitcher.Windows(tokenCount: 0, maxTokens: 8, overlap: 2);

        Assert.Empty(windows);
    }

    [Theory]
    [InlineData(4, 4)]   // overlap == maxTokens
    [InlineData(4, 5)]   // overlap > maxTokens
    [InlineData(4, -1)]  // overlap negative
    [InlineData(0, 0)]   // maxTokens not positive
    public void Windows_InvalidArguments_Throw(int maxTokens, int overlap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TokenWindowStitcher.Windows(tokenCount: 10, maxTokens, overlap));
    }

    // ── Stitch: matrix assembly ──────────────────────────────────────────────

    /// <summary>Matrix for a window where every row is filled with <paramref name="tag"/>.</summary>
    private static float[] TaggedMatrix(int rows, int dimension, float tag)
    {
        var matrix = new float[rows * dimension];
        Array.Fill(matrix, tag);
        return matrix;
    }

    [Fact]
    public void Stitch_SingleWindow_CopiesMatrixVerbatim()
    {
        float[] matrix = [1f, 2f, 3f, 4f, 5f, 6f];

        var stitched = TokenWindowStitcher.Stitch([matrix], [(0, 3)], dimension: 2, totalTokens: 3);

        Assert.Equal(matrix, stitched);
    }

    [Fact]
    public void Stitch_OverlapRegions_KeepLaterWindowVectors()
    {
        // Windows (0,4) and (3,7) overlap on token 3. Window 0 rows are tagged 1, window 1
        // rows tagged 2: token 3 must carry the LATER window's vector (more right-context).
        var windows = new List<(int Start, int End)> { (0, 4), (3, 7) };
        var matrices = new List<float[]>
        {
            TaggedMatrix(rows: 4, dimension: 2, tag: 1f),
            TaggedMatrix(rows: 4, dimension: 2, tag: 2f),
        };

        var stitched = TokenWindowStitcher.Stitch(matrices, windows, dimension: 2, totalTokens: 7);

        Assert.Equal(14, stitched.Length);
        float[] expectedRowTags = [1f, 1f, 1f, 2f, 2f, 2f, 2f];
        for (var token = 0; token < 7; token++)
        {
            Assert.Equal(expectedRowTags[token], stitched[token * 2]);
            Assert.Equal(expectedRowTags[token], stitched[(token * 2) + 1]);
        }
    }

    [Fact]
    public void Stitch_ThreeWindows_EveryOverlapComesFromLaterWindow()
    {
        // (0,4), (3,7), (6,10): tokens 3 and 6 are overlaps → tags 2 and 3 respectively.
        var windows = new List<(int Start, int End)> { (0, 4), (3, 7), (6, 10) };
        var matrices = new List<float[]>
        {
            TaggedMatrix(4, 1, 1f),
            TaggedMatrix(4, 1, 2f),
            TaggedMatrix(4, 1, 3f),
        };

        var stitched = TokenWindowStitcher.Stitch(matrices, windows, dimension: 1, totalTokens: 10);

        Assert.Equal([1f, 1f, 1f, 2f, 2f, 2f, 3f, 3f, 3f, 3f], stitched);
    }

    [Fact]
    public void Stitch_WindowCountMismatch_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TokenWindowStitcher.Stitch([TaggedMatrix(3, 2, 1f)], [(0, 3), (2, 5)], dimension: 2, totalTokens: 5));

        Assert.Contains("window", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stitch_MatrixSizeMismatch_Throws()
    {
        // Window (0,3) with dimension 2 needs 6 floats; give it 4.
        Assert.Throws<ArgumentException>(() =>
            TokenWindowStitcher.Stitch([new float[4]], [(0, 3)], dimension: 2, totalTokens: 3));
    }

    [Fact]
    public void Stitch_WindowsNotCoveringAllTokens_Throws()
    {
        // Last window ends at 3 but totalTokens is 5 → rows 3..4 would stay zero-filled.
        Assert.Throws<ArgumentException>(() =>
            TokenWindowStitcher.Stitch([TaggedMatrix(3, 2, 1f)], [(0, 3)], dimension: 2, totalTokens: 5));
    }

    [Fact]
    public void Stitch_NonPositiveDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TokenWindowStitcher.Stitch([TaggedMatrix(3, 1, 1f)], [(0, 3)], dimension: 0, totalTokens: 3));
    }
}
