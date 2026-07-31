using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class RecursiveChunkingStrategyTests
{
    private readonly RecursiveChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId("doc-1"),
        SectionIndex = 0
    };

    [Fact]
    public async Task ChunkAsync_SplitsByParagraphsFirst()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0].Text);
        Assert.Equal("Second paragraph.", chunks[1].Text);
    }

    [Fact]
    public async Task ChunkAsync_FallsBackToSentences_WhenParagraphTooLong()
    {
        var text = "First sentence. Second sentence. Third sentence.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 20));
    }

    [Fact]
    public async Task ChunkAsync_ShortText_ReturnsSingleChunk()
    {
        var section = CreateSection("Hello.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("Hello.", chunks[0].Text);
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentIdAndChunkIndex()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[1].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ChunksOverlap()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 5 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("First paragraph.", chunks[0].Text);
        // The second chunk should start with the last 5 chars of the first chunk's text
        var expectedOverlap = chunks[0].Text[^5..];
        Assert.StartsWith(expectedOverlap, chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkAsync_TracksPositionsRelativeToSource()
    {
        var text = "First paragraph.\n\nSecond paragraph.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunks.Count);

        // First chunk: "First paragraph." starts at 0 in source
        Assert.Equal(0, chunks[0].StartPosition);
        Assert.Equal("First paragraph.".Length, chunks[0].EndPosition);

        // Second chunk: "Second paragraph." starts after "\n\n" separator
        var expectedStart = text.IndexOf("Second paragraph.", StringComparison.Ordinal);
        Assert.Equal(expectedStart, chunks[1].StartPosition);
        Assert.Equal(expectedStart + "Second paragraph.".Length, chunks[1].EndPosition);
    }

    [Fact]
    public async Task ChunkAsync_TextShorterThanMaxChunkSize_IsNotSplitAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        // 35 characters against a 512 limit. There is nothing to split.
        var section = CreateSection("First paragraph.\n\nSecond paragraph.");
        var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal("First paragraph.\n\nSecond paragraph.", chunks[0].Text);
    }

    [Fact]
    public async Task ChunkAsync_NoSeparatorFound_HardSplitsAtMaxSize()
    {
        var ct = TestContext.Current.CancellationToken;
        // 25 'a' chars with MaxChunkSize=10 and no valid separator — must hard-split
        var text = new string('a', 25);
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.True(chunks.Count >= 2, "Hard split must produce multiple chunks");
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 10, $"Chunk too long: '{c.Text}'"));
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task ChunkAsync_ManyShortLines_PacksThemTowardsMaxChunkSize()
    {
        var ct = TestContext.Current.CancellationToken;
        // 60 lines of ~16 characters = 1,010 characters against a 512 limit.
        var text = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"- item number {i}"));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Before this task: 60 chunks averaging 31 characters.
        Assert.InRange(chunks.Count, 2, 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 512, $"Chunk of {c.Text.Length} exceeds 512"));
    }

    [Fact]
    public async Task ChunkAsync_WordsWithNoSentenceOrLineBreak_DoNotBecomeOneChunkPerWord()
    {
        var ct = TestContext.Current.CancellationToken;
        // 150 words, no ". " and no newline, so the recursion reaches the " " separator.
        var text = string.Join(" ", Enumerable.Repeat("word", 150));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Before this task: 150 chunks of 4 characters. This is the case that proves it is a defect.
        Assert.InRange(chunks.Count, 2, 3);
        // Greedy packing fills every chunk towards MaxChunkSize; only the final
        // chunk may carry the short tail (749 chars total cannot make two chunks
        // that both exceed 400).
        Assert.All(chunks[..^1], c => Assert.True(c.Text.Length > 400, $"Chunk of {c.Text.Length} was not packed"));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 512, $"Chunk of {c.Text.Length} exceeds 512"));
    }

    [Fact]
    public async Task ChunkAsync_SentencesArePackedAndKeepTheirSeparators()
    {
        var ct = TestContext.Current.CancellationToken;
        var text = "Alpha one. Bravo two. Charlie three. Delta four.";
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 30, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Sentences pack until the next will not fit, and the ". " between packed
        // sentences is restored rather than dropped.
        Assert.Contains(chunks, c => c.Text.Contains(". ", StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 30, $"Chunk of {c.Text.Length} exceeds 30"));
    }

    [Fact]
    public async Task ChunkAsync_PartsThatExactlyFillMaxChunkSize_PackIntoOneChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        // Exact-boundary pin for the fit check `buffer + separator + next <= maxSize`.
        // Lines of 9 and 10 chars joined by "\n" (1 char): 9 + 1 + 10 = 20 = MaxChunkSize
        // exactly — the case where `<=` and `<` disagree. The third 10-char line makes
        // the whole text 31 chars so splitting actually happens, and it cannot join
        // (20 + 1 + 10 = 31 > 20), so it must land alone in the second chunk.
        var text = new string('a', 9) + "\n" + new string('b', 10) + "\n" + new string('c', 10);
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('a', 9) + "\n" + new string('b', 10), chunks[0].Text);
        Assert.Equal(20, chunks[0].Text.Length);
        Assert.Equal(new string('c', 10), chunks[1].Text);
    }

    [Fact]
    public async Task ChunkAsync_PartsOneCharOverMaxChunkSize_SplitIntoTwoChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        // The other side of the boundary: two 10-char lines joined by "\n" (1 char)
        // sum to 10 + 1 + 10 = 21 = MaxChunkSize + 1. Only the separator's own length
        // pushes this over the limit, so a fit check that forgets `separator.Length`
        // (10 + 10 = 20 <= 20) would pack them into a 21-char chunk exceeding MaxChunkSize.
        var text = new string('a', 10) + "\n" + new string('b', 10);
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new string('a', 10), chunks[0].Text);
        Assert.Equal(new string('b', 10), chunks[1].Text);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 20, $"Chunk of {c.Text.Length} exceeds 20"));
    }
}
