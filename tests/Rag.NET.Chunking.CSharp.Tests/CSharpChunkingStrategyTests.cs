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
        var methodChunk = chunks.Single(c => c.Metadata.TryGetValue("csharp.kind", out var k) && k == "method");

        Assert.Equal<MetadataValue>("MyApp.Core", methodChunk.Metadata["csharp.namespace"]);
        Assert.Equal<MetadataValue>("Greeter", methodChunk.Metadata["csharp.type"]);
        Assert.Equal<MetadataValue>("Greet", methodChunk.Metadata["csharp.name"]);
        Assert.Equal<MetadataValue>("method", methodChunk.Metadata["csharp.kind"]);
        Assert.Equal<MetadataValue>("public", methodChunk.Metadata["csharp.accessibility"]);
    }

    [Fact]
    public async Task ChunkAsync_PrivateMember_ExcludedByDefault()
    {
        const string source = """
            public class Foo
            {
                public void Public() { }
                private void Private() { }
            }
            """;

        var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("csharp.name", out var n) && n == "Private");
    }

    [Fact]
    public async Task ChunkAsync_PrivateMember_IncludedWhenOptionSet()
    {
        const string source = """
            public class Foo
            {
                public void Public() { }
                private void Private() { }
            }
            """;

        var chunks = await Strategy(new CSharpChunkingOptions { IncludePrivateMembers = true })
            .ChunkAsync(Section(source), DefaultOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(chunks, c =>
            c.Metadata.TryGetValue("csharp.name", out var n) && n == "Private");
    }

    [Fact]
    public async Task ChunkAsync_XmlDoc_ExtractedToSummaryMetadata()
    {
        const string source = """
            public class Greeter
            {
                /// <summary>Says hello to the given name.</summary>
                public string Greet(string name) => $"Hello {name}";
            }
            """;

        var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var greet = chunks.Single(c =>
            c.Metadata.TryGetValue("csharp.name", out var n) && n == "Greet");

        Assert.Equal<MetadataValue>("Says hello to the given name.", greet.Metadata["csharp.summary"]);
    }

    [Fact]
    public async Task ChunkAsync_NestedClass_YieldsOuterAndInnerSeparately()
    {
        const string source = """
            public class Outer
            {
                public class Inner
                {
                    public void InnerMethod() { }
                }
            }
            """;

        var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var names = chunks.Select(c => c.Metadata["csharp.name"]).ToList();

        Assert.Contains((MetadataValue)"Outer", names);
        Assert.Contains((MetadataValue)"Inner", names);
        Assert.Contains((MetadataValue)"InnerMethod", names);
    }

    [Fact]
    public async Task ChunkAsync_InternalMember_ExcludedWhenOptionSet()
    {
        const string source = """
            public class Foo
            {
                public void Public() { }
                internal void Internal() { }
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var chunks = await Strategy(new CSharpChunkingOptions { IncludeInternalMembers = false })
            .ChunkAsync(Section(source), DefaultOptions, ct).ToListAsync(ct);

        Assert.DoesNotContain(chunks, c =>
            c.Metadata.TryGetValue("csharp.name", out var n) && n == "Internal");
    }

    [Fact]
    public async Task ChunkAsync_IncludeBodiesFalse_StripsMethodBody()
    {
        const string source = """
            public class Foo
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var chunks = await Strategy(new CSharpChunkingOptions { IncludeBodies = false })
            .ChunkAsync(Section(source), DefaultOptions, ct).ToListAsync(ct);

        var method = chunks.Single(c =>
            c.Metadata.TryGetValue("csharp.name", out var n) && n == "Add");

        Assert.DoesNotContain("return a + b", method.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkAsync_Positions_MatchSourceOffsets()
    {
        const string source = """
            namespace MyApp;

            public class Calc
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions, ct).ToListAsync(ct);

        foreach (var chunk in chunks)
        {
            Assert.True(chunk.StartPosition >= 0);
            Assert.True(chunk.EndPosition > chunk.StartPosition);
            Assert.True(chunk.EndPosition <= source.Length);
        }
    }

    [Fact]
    public async Task ChunkAsync_OversizedMember_YieldsWithOversizedFlag()
    {
        var body = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"    var x{i} = {i};"));
        var source = $$"""
            public class Big
            {
                public void HugeMethod()
                {
            {{body}}
                }
            }
            """;

        var tinyOptions = new ChunkingOptions { MaxChunkSize = 50 };
        var chunks = await Strategy().ChunkAsync(Section(source), tinyOptions, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var huge = chunks.Single(c =>
            c.Metadata.TryGetValue("csharp.name", out var n) && n == "HugeMethod");

        Assert.Equal<MetadataValue>("true", huge.Metadata["csharp.oversized"]);
    }
}
