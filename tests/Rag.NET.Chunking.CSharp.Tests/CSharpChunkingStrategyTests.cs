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

    [Fact]
    public async Task ChunkAsync_SimpleClass_YieldsOneChunkPerMember()
    {
        const string source = """
            namespace MyApp;

            public class Calculator
            {
                public int Add(int a, int b) => a + b;

                public string Name { get; set; } = "calc";
            }
            """;

        var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // class + method + property = 3 chunks
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public async Task ChunkAsync_SimpleClass_MetadataKeys_CorrectNamespaceAndKind()
    {
        const string source = """
            namespace MyApp.Core;

            public class Greeter
            {
                public string Greet(string name) => $"Hello {name}";
            }
            """;

        var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var methodChunk = chunks.Single(c => c.Metadata.TryGetValue("csharp.kind", out var k) && string.Equals(k, "method", StringComparison.Ordinal));

        Assert.Equal("MyApp.Core", methodChunk.Metadata["csharp.namespace"]);
        Assert.Equal("Greeter", methodChunk.Metadata["csharp.type"]);
        Assert.Equal("Greet", methodChunk.Metadata["csharp.name"]);
        Assert.Equal("method", methodChunk.Metadata["csharp.kind"]);
        Assert.Equal("public", methodChunk.Metadata["csharp.accessibility"]);
    }
}
