using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticRefinementStrategyTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(params float[][] vectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                for (int i = 0; i < inputs.Count; i++)
                    result.Add(new Embedding<float>(i < vectors.Length ? vectors[i] : vectors[^1]));
                return Task.FromResult(result);
            });
        return embedder;
    }

    private static async IAsyncEnumerable<TextChunk> ToAsync(IEnumerable<TextChunk> chunks)
    {
        foreach (var c in chunks) yield return c;
    }

    [Fact]
    public void SemanticChunkingStrategy_ImplementsIChunkRefinementStrategy()
    {
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());
        Assert.IsAssignableFrom<IChunkRefinementStrategy>(strategy);
    }

    [Fact]
    public async Task RefineAsync_EmptyInput_ProducesNoChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());

        var result = await strategy.RefineAsync(ToAsync([]), ct).ToListAsync(ct);

        Assert.Empty(result);
    }

    [Fact]
    public async Task RefineAsync_ShortChunk_PassesThroughUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        // Chunk shorter than MinChunkSize must not be sub-split
        var opts = new SemanticChunkingOptions { MinChunkSize = 1000 };
        var strategy = new SemanticChunkingStrategy(MockEmbedder([1f, 0f]), opts);

        var chunk = new TextChunk
        {
            Text = "Short text.",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = 11,
        };

        var result = await strategy.RefineAsync(ToAsync([chunk]), ct).ToListAsync(ct);

        Assert.Single(result);
        Assert.Equal("Short text.", result[0].Text);
    }

    [Fact]
    public async Task RefineAsync_LongChunk_SubSplitsAtSentenceBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        // Chunk text longer than MinChunkSize; embedder returns vectors where the first
        // consecutive pair is orthogonal (similarity=0) and the second is identical (similarity=1).
        // With a high BreakpointPercentile the threshold equals the max similarity (1.0),
        // so the low-similarity boundary triggers a split.
        var opts = new SemanticChunkingOptions { MinChunkSize = 5, MaxChunkSize = 5000, BreakpointPercentile = 0.95f };
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [0f, 1f], [0f, 1f]),
            opts);

        // Build text that will split into 3 sentences and exceed MinChunkSize
        var text = "First sentence here. Second sentence there. Third sentence ok.";
        var chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = text.Length,
        };

        var result = await strategy.RefineAsync(ToAsync([chunk]), ct).ToListAsync(ct);

        // Must produce more than 1 chunk when text is sub-split
        Assert.True(result.Count > 1);
        Assert.All(result, c => Assert.Equal(new DocumentId("doc1"), c.DocumentId));
    }

    [Fact]
    public async Task RefineAsync_MultipleChunks_ChunkIndexIsGloballySequential()
    {
        var ct = TestContext.Current.CancellationToken;
        // Short chunk (passes through) + short chunk (passes through) = indices 0, 1
        var opts = new SemanticChunkingOptions { MinChunkSize = 1000 };
        var strategy = new SemanticChunkingStrategy(MockEmbedder([1f, 0f]), opts);

        var chunks = new[]
        {
            new TextChunk { Text = "First.", DocumentId = new DocumentId("doc1"), ChunkIndex = 0, StartPosition = 0, EndPosition = 6 },
            new TextChunk { Text = "Second.", DocumentId = new DocumentId("doc1"), ChunkIndex = 0, StartPosition = 0, EndPosition = 7 },
        };

        var result = await strategy.RefineAsync(ToAsync(chunks), ct).ToListAsync(ct);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].ChunkIndex);
        Assert.Equal(1, result[1].ChunkIndex);
    }

    [Fact]
    public async Task RefineAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 5 });

        var text = "First sentence here. Second sentence there. Third sentence ok.";
        var chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = text.Length,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await strategy.RefineAsync(ToAsync([chunk]), cts.Token).ToListAsync(cts.Token));
    }

    [Fact]
    public async Task RefineAsync_MixedShortAndLongChunks_ReindexesSequentially()
    {
        var ct = TestContext.Current.CancellationToken;
        // short (pass-through) + long (sub-split into 2) + short (pass-through)
        // Expected output indices: 0, 1, 2, 3
        var opts = new SemanticChunkingOptions { MinChunkSize = 5, MaxChunkSize = 5000, BreakpointPercentile = 0.95f };
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [0f, 1f], [0f, 1f]),
            opts);

        var longText = "First sentence here. Second sentence there. Third sentence ok.";
        var chunks = new[]
        {
            new TextChunk { Text = "Hi.", DocumentId = new DocumentId("doc1"), ChunkIndex = 0, StartPosition = 0, EndPosition = 3 },
            new TextChunk { Text = longText, DocumentId = new DocumentId("doc1"), ChunkIndex = 1, StartPosition = 0, EndPosition = longText.Length },
            new TextChunk { Text = "Bye.", DocumentId = new DocumentId("doc1"), ChunkIndex = 2, StartPosition = 0, EndPosition = 4 },
        };

        var result = await strategy.RefineAsync(ToAsync(chunks), ct).ToListAsync(ct);

        // "Hi." passes through (index 0)
        // longText sub-splits into ≥2 chunks (indices 1, 2, ...)
        // "Bye." passes through (last index)
        Assert.True(result.Count >= 4); // at least 4 chunks: 1 + 2+ + 1
        // ChunkIndex must be globally sequential: 0, 1, 2, ...
        for (int i = 0; i < result.Count; i++)
            Assert.Equal(i, result[i].ChunkIndex);
        // First chunk is "Hi."
        Assert.Equal("Hi.", result[0].Text);
        // Last chunk is "Bye."
        Assert.Equal("Bye.", result[^1].Text);
    }
}
