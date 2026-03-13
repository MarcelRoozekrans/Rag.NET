using Microsoft.ML.Tokenizers;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class TokenAwareChunkingStrategyTests
{
    private readonly TokenAwareChunkingStrategy _sut = new();

    private static DocumentSection CreateSection(string text) => new()
    {
        Text = text,
        DocumentId = "doc-1",
        SectionIndex = 0,
    };

    [Fact]
    public void Constructor_NullModelName_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new TokenAwareChunkingStrategy(null!));
    }

    [Fact]
    public void Constructor_EmptyModelName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new TokenAwareChunkingStrategy(""));
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var section = CreateSection("");
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_TextShorterThanMax_ReturnsSingleChunk()
    {
        var section = CreateSection("Hello world");
        var options = new ChunkingOptions { MaxChunkSize = 100, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(chunks);
        Assert.Equal("doc-1", chunks[0].DocumentId);
        Assert.Equal(0, chunks[0].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_AllChunksWithinTokenLimit()
    {
        // Dense text that might exceed char-based limits
        var denseText = string.Join(" ", Enumerable.Repeat("https://example.com/very/long/url/path?query=value&another=param", 20));
        var section = CreateSection(denseText);
        var options = new ChunkingOptions { MaxChunkSize = 50, Overlap = 0 };
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            var tokenCount = tokenizer.CountTokens(chunk.Text);
            Assert.True(tokenCount <= options.MaxChunkSize,
                $"Chunk has {tokenCount} tokens, expected <= {options.MaxChunkSize}");
        }
    }

    [Fact]
    public async Task ChunkAsync_AssignsIncrementingChunkIndex()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 200));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(chunks.Count >= 2);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
        }
    }

    [Fact]
    public async Task ChunkAsync_PreservesDocumentId()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var section = CreateSection(text);
        var options = new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 };

        var chunks = await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId));
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ProducesMoreChunksThanWithout()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 100));
        var section = CreateSection(text);

        var withoutOverlap = await _sut.ChunkAsync(section, new ChunkingOptions { MaxChunkSize = 20, Overlap = 0 }, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var withOverlap = await _sut.ChunkAsync(section, new ChunkingOptions { MaxChunkSize = 20, Overlap = 5 }, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.True(withOverlap.Count > withoutOverlap.Count);
    }

    [Fact]
    public async Task ChunkAsync_OverlapGreaterThanOrEqualToMaxChunkSize_ThrowsArgumentOutOfRangeException()
    {
        var section = new DocumentSection { Text = "some text", DocumentId = "d", SectionIndex = 0 };
        var options = new ChunkingOptions { MaxChunkSize = 10, Overlap = 10 };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChunkAsync_MaxChunkSizeZero_ThrowsArgumentOutOfRangeException()
    {
        var section = new DocumentSection { Text = "some text", DocumentId = "d", SectionIndex = 0 };
        var options = new ChunkingOptions { MaxChunkSize = 0, Overlap = 0 };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await _sut.ChunkAsync(section, options, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }
}
