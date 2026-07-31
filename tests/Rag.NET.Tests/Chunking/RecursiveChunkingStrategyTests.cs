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
}
