using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Chunking.CSharp;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.CSharp.Tests;

public class CSharpChunkingStrategyTests
{
    private static readonly ChunkingOptions DefaultOptions = new();

    private static DocumentSection Section(string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId("test.cs"),
    };

    private static CSharpChunkingStrategy Strategy(CSharpChunkingOptions? opts = null)
        => new(opts ?? new CSharpChunkingOptions(), NullLogger<CSharpChunkingStrategy>.Instance);

    [Fact]
    public async Task ChunkAsync_EmptyInput_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var chunks = await Strategy().ChunkAsync(Section(""), DefaultOptions, ct).ToListAsync(ct);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_WhitespaceInput_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var chunks = await Strategy().ChunkAsync(Section("   \n  "), DefaultOptions, ct).ToListAsync(ct);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_ParseError_YieldsFallbackChunk()
    {
        // Invalid C# — not a valid compilation unit
        var ct = TestContext.Current.CancellationToken;
        var chunks = await Strategy().ChunkAsync(Section("this is not valid C# @@@"), DefaultOptions, ct).ToListAsync(ct);
        Assert.Single(chunks);
        Assert.Equal("this is not valid C# @@@", chunks[0].Text);
    }
}
