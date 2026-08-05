using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticChunkingStrategyDocumentTests
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
                var inputs = ci.Arg<IEnumerable<string>>()!.ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                for (int i = 0; i < inputs.Count; i++)
                    result.Add(new Embedding<float>(i < vectors.Length ? vectors[i] : vectors[^1]));
                return Task.FromResult(result);
            });
        return embedder;
    }

    private static DocumentSection Section(string docId, string text) =>
        new() { DocumentId = new DocumentId(docId), Text = text };

    private static async IAsyncEnumerable<DocumentSection> ToAsync(
        IEnumerable<DocumentSection> sections)
    {
        foreach (var s in sections) yield return s;
    }

    [Fact]
    public void SemanticChunkingStrategy_ImplementsIDocumentChunkingStrategy()
    {
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());
        Assert.IsAssignableFrom<IDocumentChunkingStrategy>(strategy);
    }

    [Fact]
    public async Task ChunkDocumentAsync_EmptySections_ProducesNoChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync([]), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkDocumentAsync_SimilarSections_MergeIntoOneChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        // Both sections get identical embeddings → similarity = 1.0 → no breakpoint → one chunk
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 1 });

        var sections = new[]
        {
            Section("doc1", "Alpha text here."),
            Section("doc1", "Beta text there."),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Contains("Alpha", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Beta", chunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkDocumentAsync_DissimilarSections_ProduceSeparateChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        // Four sections: first three are identical (similarity=1.0), fourth is anti-parallel (similarity=-1.0).
        // With BreakpointPercentile=0.5 and 3 similarities [1.0, 1.0, -1.0]:
        //   sorted = [-1.0, 1.0, 1.0], thresholdIndex = floor(0.5*3)=1, threshold = 1.0
        //   similarities[2]=-1.0 < 1.0 → split at index 2 → two groups → two chunks
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f], [1f, 0f], [-1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 1, BreakpointPercentile = 0.5f });

        var sections = new[]
        {
            Section("doc1", "Alpha topic sentence one."),
            Section("doc1", "Alpha topic sentence two."),
            Section("doc1", "Alpha topic sentence three."),
            Section("doc1", "Beta topic sentence four."),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task ChunkDocumentAsync_OversizedGroup_SplitsChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        // Three sections with identical embeddings (no breakpoints) → one group with 3 items.
        // Each item is 15 chars; joined total exceeds MaxChunkSize=20 → SplitOversizedGroups splits into sub-groups.
        var opts = new SemanticChunkingOptions { MinChunkSize = 1, MaxChunkSize = 20 };
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f], [1f, 0f]), opts);

        var sections = new[]
        {
            Section("doc1", new string('A', 15)),
            Section("doc1", new string('B', 15)),
            Section("doc1", new string('C', 15)),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions(), ct).ToListAsync(ct);

        // Expect more than 1 chunk because combined length (47 chars with \n\n separators) exceeds MaxChunkSize
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public async Task ChunkDocumentAsync_UndersizedGroup_MergesWithNeighbor()
    {
        var ct = TestContext.Current.CancellationToken;
        // Three sections: tiny, tiny, large; tiny sections should merge due to MinChunkSize
        // All identical embeddings to avoid breakpoints; rely on merge pass only
        var opts = new SemanticChunkingOptions { MinChunkSize = 30 };
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f], [1f, 0f]),
            opts);

        var sections = new[]
        {
            Section("doc1", "Hi."),
            Section("doc1", "Ok."),
            Section("doc1", "This is a longer section with sufficient content to meet min."),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions(), ct).ToListAsync(ct);

        // Tiny sections should have been merged; expect fewer than 3 chunks
        Assert.True(chunks.Count < 3);
    }

    [Fact]
    public async Task ChunkDocumentAsync_SingleSection_ProducesOneChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 1 });

        var sections = new[] { Section("doc1", "Only one section here.") };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        Assert.Equal("Only one section here.", chunks[0].Text);
        Assert.Equal(new DocumentId("doc1"), chunks[0].DocumentId);
    }
}
