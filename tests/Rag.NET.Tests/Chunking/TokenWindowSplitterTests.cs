using Rag.NET.Chunking;
using Xunit;

namespace Rag.NET.Tests.Chunking;

/// <summary>
/// Direct unit tests for the internal <see cref="TokenWindowSplitter"/>: partition mode
/// (overlap 0 — contiguous spans that exactly partition the text, pinned indirectly by the
/// proposition-passage tests) and overlap mode (overlapping per-window token spans, used by
/// late chunking), including degenerate token offsets.
/// </summary>
public class TokenWindowSplitterTests
{
    /// <summary>Contiguous word-like offsets: n tokens of width w, no gaps.</summary>
    private static (int Start, int End)[] ContiguousOffsets(int count, int width)
    {
        var offsets = new (int Start, int End)[count];
        for (var i = 0; i < count; i++)
            offsets[i] = (i * width, (i + 1) * width);
        return offsets;
    }

    // ── Partition mode (overlapTokens == 0) ──────────────────────────────────

    [Fact]
    public void Partition_ExactCover_SpansPartitionTheText()
    {
        var offsets = ContiguousOffsets(count: 6, width: 5); // covers [0..30)
        var windows = TokenWindowSplitter.Split(offsets, textLength: 30, windowTokens: 2, overlapTokens: 0);

        Assert.Equal(3, windows.Count);
        Assert.Equal(0, windows[0].Start);
        Assert.Equal(30, windows[^1].End);
        for (var i = 1; i < windows.Count; i++)
            Assert.Equal(windows[i - 1].End, windows[i].Start);

        // Token ranges are the exact partition [0..1], [2..3], [4..5].
        Assert.Equal((0, 1), (windows[0].FirstToken, windows[0].LastToken));
        Assert.Equal((2, 3), (windows[1].FirstToken, windows[1].LastToken));
        Assert.Equal((4, 5), (windows[2].FirstToken, windows[2].LastToken));
    }

    [Fact]
    public void Partition_FinalWindow_IsForcedToTextEnd()
    {
        // Last token ends at 10 but the text is longer (trailing chars outside any token).
        (int Start, int End)[] offsets = [(0, 5), (5, 10)];
        var windows = TokenWindowSplitter.Split(offsets, textLength: 14, windowTokens: 1, overlapTokens: 0);

        Assert.Equal(2, windows.Count);
        Assert.Equal(14, windows[^1].End); // forced past the last token's End
        Assert.Equal(windows[0].End, windows[1].Start);
    }

    [Fact]
    public void Partition_ZeroAdvanceWindow_IsFolded()
    {
        // All four tokens share one span (e.g. tokens inside one multi-byte char): the second
        // window advances zero chars past the covered end and must be skipped, leaving a
        // single window that still partitions the whole text.
        (int Start, int End)[] offsets = [(0, 10), (0, 10), (0, 10), (0, 10)];
        var windows = TokenWindowSplitter.Split(offsets, textLength: 10, windowTokens: 2, overlapTokens: 0);

        var window = Assert.Single(windows);
        Assert.Equal(0, window.Start);
        Assert.Equal(10, window.End);
    }

    [Fact]
    public void Partition_RemainderWindow_IsShorter()
    {
        var offsets = ContiguousOffsets(count: 5, width: 4); // 5 tokens, window 3 → [0..2], [3..4]
        var windows = TokenWindowSplitter.Split(offsets, textLength: 20, windowTokens: 3, overlapTokens: 0);

        Assert.Equal(2, windows.Count);
        Assert.Equal((0, 2), (windows[0].FirstToken, windows[0].LastToken));
        Assert.Equal((3, 4), (windows[1].FirstToken, windows[1].LastToken));
        Assert.Equal(0, windows[0].Start);
        Assert.Equal(windows[0].End, windows[1].Start);
        Assert.Equal(20, windows[1].End);
    }

    // ── Overlap mode (overlapTokens > 0) ─────────────────────────────────────

    [Fact]
    public void Overlap_ExactStepMath_Window4Overlap1Over10Tokens()
    {
        var offsets = ContiguousOffsets(count: 10, width: 2); // covers [0..20)
        var windows = TokenWindowSplitter.Split(offsets, textLength: 20, windowTokens: 4, overlapTokens: 1);

        // Step 3 → token windows [0..3], [3..6], [6..9]; iteration stops at the final token.
        Assert.Equal(3, windows.Count);
        Assert.Equal((0, 3), (windows[0].FirstToken, windows[0].LastToken));
        Assert.Equal((3, 6), (windows[1].FirstToken, windows[1].LastToken));
        Assert.Equal((6, 9), (windows[2].FirstToken, windows[2].LastToken));

        // Each span is [firstTokenStart .. lastTokenEnd]; adjacent spans overlap by one token.
        Assert.Equal((0, 8), (windows[0].Start, windows[0].End));
        Assert.Equal((6, 14), (windows[1].Start, windows[1].End));
        Assert.Equal((12, 20), (windows[2].Start, windows[2].End));
    }

    [Fact]
    public void Overlap_SingleShortWindow_WhenTokenCountBelowWindowSize()
    {
        var offsets = ContiguousOffsets(count: 3, width: 5);
        var windows = TokenWindowSplitter.Split(offsets, textLength: 15, windowTokens: 10, overlapTokens: 2);

        var window = Assert.Single(windows);
        Assert.Equal((0, 2), (window.FirstToken, window.LastToken));
        Assert.Equal((0, 15), (window.Start, window.End));
    }

    [Fact]
    public void Overlap_RepeatedTokenOffsets_ZeroAdvanceWindowsAreSkipped()
    {
        // Three tokens share span (0,5): windows that do not advance the covered end are
        // dropped instead of emitting duplicate spans.
        (int Start, int End)[] offsets = [(0, 5), (0, 5), (0, 5), (5, 9)];
        var windows = TokenWindowSplitter.Split(offsets, textLength: 9, windowTokens: 2, overlapTokens: 1);

        Assert.Equal(2, windows.Count);
        Assert.Equal((0, 5), (windows[0].Start, windows[0].End));
        Assert.Equal((0, 9), (windows[1].Start, windows[1].End)); // window [2..3] spans both
        // Strictly increasing covered end — no zero-advance duplicates.
        Assert.True(windows[1].End > windows[0].End);
    }

    [Fact]
    public void Overlap_DegenerateOffsets_EndClampsToStart()
    {
        // Out-of-order/degenerate offsets: the last token of the window ends BEFORE the first
        // token starts. End clamps to Start (never a negative-length span).
        (int Start, int End)[] offsets = [(5, 9), (0, 2)];
        var windows = TokenWindowSplitter.Split(offsets, textLength: 9, windowTokens: 2, overlapTokens: 1);

        var window = Assert.Single(windows);
        Assert.Equal((0, 1), (window.FirstToken, window.LastToken));
        Assert.Equal(5, window.Start);
        Assert.Equal(window.Start, window.End); // clamped: tokenOffsets[last].End (2) < start (5)
    }

    [Fact]
    public void Overlap_EmptyOffsets_YieldNoWindows()
    {
        var windows = TokenWindowSplitter.Split([], textLength: 0, windowTokens: 4, overlapTokens: 1);

        Assert.Empty(windows);
    }
}
