using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class FixedSizeChunkingStrategyTests
{
    private readonly FixedSizeChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = "doc-1",
        SectionIndex = 0
    };

    [Fact]
    public async Task ChunkAsync_TextShorterThanMax_ReturnsSingleChunk()
    {
        var section = CreateSection("Short text.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("Short text.", chunks[0].Text);
        Assert.Equal(0, chunks[0].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_TextLongerThanMax_SplitsIntoMultipleChunks()
    {
        var section = CreateSection("AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC");
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Text.Length <= 10);
        }
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ChunksOverlap()
    {
        var text = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"word{i}"));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 30, Overlap = 10 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(chunks.Count >= 2);
        for (int i = 1; i < chunks.Count; i++)
        {
            Assert.True(chunks[i].StartPosition < chunks[i - 1].EndPosition,
                "Chunks should overlap");
        }
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentId()
    {
        var section = CreateSection("Some text here.");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
    }

    [Fact]
    public async Task ChunkAsync_AssignsIncrementingChunkIndex()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
        }
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }
}
