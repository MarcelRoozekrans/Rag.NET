using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.CSharp;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.IntegrationTests;

public sealed class ChunkingStrategyTests
{
    // ── TokenAwareChunkingStrategy ────────────────────────────────────────────

    [Fact]
    public async Task TokenAware_LongText_ProducesMultipleChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new TokenAwareChunkingStrategy();

        // 500 repetitions of "word " produces well over 100 tokens
        var text = string.Join(" ", Enumerable.Repeat("word", 500));
        var section = new DocumentSection
        {
            Text = text,
            DocumentId = new DocumentId("test-doc"),
            SectionIndex = 0,
        };
        var options = new ChunkingOptions { MaxChunkSize = 50, Overlap = 0 };

        var chunks = await sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    // ── CSharpChunkingStrategy ────────────────────────────────────────────────

    [Fact]
    public async Task CSharp_SampleFile_ProducesOneChunkPerMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new CSharpChunkingStrategy(
            new CSharpChunkingOptions(),
            NullLogger<CSharpChunkingStrategy>.Instance);

        var source = LoadResource("sample.cs");
        var section = new DocumentSection
        {
            Text = source,
            DocumentId = new DocumentId("sample.cs"),
            SectionIndex = 0,
        };
        var options = new ChunkingOptions();

        var chunks = await sut.ChunkAsync(section, options, ct).ToListAsync(ct);

        // Calculator class + Add + Subtract + Multiply = 4 chunks
        Assert.Equal(4, chunks.Count);
        Assert.Contains(chunks, c => c.Text.Contains("Add", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("Subtract", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Text.Contains("Multiply", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string LoadResource(string fileName)
    {
        var assembly = typeof(ChunkingStrategyTests).Assembly;
        var resourceName = $"Rag.NET.Chunking.IntegrationTests.Resources.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
