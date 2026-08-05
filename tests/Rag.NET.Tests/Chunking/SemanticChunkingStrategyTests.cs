using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Chunking.Semantic;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticChunkingStrategyTests
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

    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var opts = new SemanticChunkingOptions();
        Assert.Equal(0.25f, opts.BreakpointPercentile);
        Assert.Equal(100, opts.MinChunkSize);
        Assert.Equal(1500, opts.MaxChunkSize);
        Assert.Null(opts.ChunkingEmbedder);
    }

    [Theory]
    [InlineData("Hello world. How are you? Fine thanks!", 3)]
    [InlineData("Single sentence without ending punctuation", 1)]
    [InlineData("Dr. Smith went to Washington. He met Mr. Jones.", 2)]
    [InlineData("", 0)]
    [InlineData("First sentence. Second sentence. Third sentence.", 3)]
    public void SplitSentences_VariousInputs_ReturnsExpectedCount(string text, int expectedCount)
    {
        var sentences = SemanticChunkingStrategy.SplitSentences(text);
        Assert.Equal(expectedCount, sentences.Count);
    }

    [Fact]
    public void SplitSentences_PreservesAbbreviations()
    {
        var sentences = SemanticChunkingStrategy.SplitSentences(
            "Dr. Smith e.g. the doctor went home. Then he slept.");
        Assert.Equal(2, sentences.Count);
        Assert.Contains("Dr. Smith", sentences[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 1f, 0f, 0f };
        var sim = SemanticChunkingStrategy.CosineSimilarity(a, b);
        Assert.Equal(1.0, sim, precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        var sim = SemanticChunkingStrategy.CosineSimilarity(a, b);
        Assert.Equal(0.0, sim, precision: 5);
    }

    [Fact]
    public async Task ChunkAsync_TwoDifferentTopics_BreaksBetweenThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = MockEmbedder(
            [1f, 0f, 0f],     // sentence 1 — topic A
            [0.9f, 0.1f, 0f], // sentence 2 — topic A (similar)
            [0f, 0f, 1f]);    // sentence 3 — topic B (different)

        var opts = new SemanticChunkingOptions { BreakpointPercentile = 0.5f, MinChunkSize = 1, MaxChunkSize = 5000 };
        var sut = new SemanticChunkingStrategy(embedder, opts);
        var section = new DocumentSection
        {
            Text = "Topic A first. Topic A second. Topic B entirely different.",
            DocumentId = new DocumentId("doc-1"),
        };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("Topic A", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("Topic B", chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkAsync_SingleSentence_ReturnsOneChunk_NoEmbeddingCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var sut = new SemanticChunkingStrategy(embedder, new SemanticChunkingOptions());
        var section = new DocumentSection
        {
            Text = "Just one sentence here",
            DocumentId = new DocumentId("doc-1"),
        };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Single(chunks);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var sut = new SemanticChunkingStrategy(embedder, new SemanticChunkingOptions());
        var section = new DocumentSection { Text = "", DocumentId = new DocumentId("doc-1") };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_UniformSimilarity_FewOrNoBreaks()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = MockEmbedder([1f, 0f], [0.99f, 0.01f], [0.98f, 0.02f], [0.97f, 0.03f]);
        var opts = new SemanticChunkingOptions { BreakpointPercentile = 0.25f, MinChunkSize = 1, MaxChunkSize = 5000 };
        var sut = new SemanticChunkingStrategy(embedder, opts);
        var section = new DocumentSection
        {
            Text = "Same topic one. Same topic two. Same topic three. Same topic four.",
            DocumentId = new DocumentId("doc-1"),
        };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        // With 4 sentences and 3 similarities, bottom 25% = at most 1 break → at most 2 chunks
        Assert.InRange(chunks.Count, 1, 2);
    }

    [Fact]
    public async Task ChunkAsync_CustomChunkingEmbedder_UsesOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        var defaultEmbedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var customEmbedder = MockEmbedder([1f, 0f], [0f, 1f]);

        var opts = new SemanticChunkingOptions
        {
            ChunkingEmbedder = customEmbedder,
            BreakpointPercentile = 0.5f,
            MinChunkSize = 1,
            MaxChunkSize = 5000,
        };
        var sut = new SemanticChunkingStrategy(defaultEmbedder, opts);
        var section = new DocumentSection
        {
            Text = "First sentence. Second sentence.",
            DocumentId = new DocumentId("doc-1"),
        };

        _ = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        await customEmbedder.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
        await defaultEmbedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkAsync_CancelledToken_ThrowsOperationCancelledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var embedder = MockEmbedder([1f, 0f], [0f, 1f]);
        var sut = new SemanticChunkingStrategy(embedder, new SemanticChunkingOptions { MinChunkSize = 1, MaxChunkSize = 5000 });
        var section = new DocumentSection
        {
            Text = "First sentence. Second sentence.",
            DocumentId = new DocumentId("doc-1"),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ChunkAsync(section, new ChunkingOptions(), cts.Token).ToListAsync(cts.Token).AsTask());
    }

    [Fact]
    public async Task ChunkAsync_SetsDocumentIdAndChunkIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = MockEmbedder([1f, 0f], [0f, 1f], [1f, 0f]);
        var opts = new SemanticChunkingOptions { BreakpointPercentile = 0.5f, MinChunkSize = 1, MaxChunkSize = 5000 };
        var sut = new SemanticChunkingStrategy(embedder, opts);
        var section = new DocumentSection
        {
            Text = "First topic. Different topic. Back to first.",
            DocumentId = new DocumentId("doc-1"),
        };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.All(chunks, c => Assert.Equal("doc-1", c.DocumentId.ToString()));
        for (int i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }

    [Fact]
    public async Task ChunkAsync_ChunkBelowMinSize_MergedWithNeighbor()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = MockEmbedder([1f, 0f], [1f, 0f], [0f, 1f]);
        var opts = new SemanticChunkingOptions
        {
            BreakpointPercentile = 0.5f,
            MinChunkSize = 200, // individual sentences are < 200 chars, so merge
            MaxChunkSize = 5000,
        };
        var sut = new SemanticChunkingStrategy(embedder, opts);
        var section = new DocumentSection
        {
            Text = "Short. Also short. Very different topic here with more words.",
            DocumentId = new DocumentId("doc-1"),
        };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        // All should merge since each chunk is below MinChunkSize
        Assert.Single(chunks);
    }

    [Fact]
    public async Task ChunkAsync_ChunkAboveMaxSize_SplitAtSentenceBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = MockEmbedder([1f, 0f], [1f, 0f], [1f, 0f], [1f, 0f]);
        var longSentence = new string('a', 400);
        var text = $"{longSentence}. {longSentence}. {longSentence}. {longSentence}.";
        var opts = new SemanticChunkingOptions
        {
            BreakpointPercentile = 0.25f,
            MinChunkSize = 1,
            MaxChunkSize = 500,
        };
        var sut = new SemanticChunkingStrategy(embedder, opts);
        var section = new DocumentSection { Text = text, DocumentId = new DocumentId("doc-1") };

        var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.True(chunks.Count > 1);
        // A single sentence exceeding MaxChunkSize is an inherent limitation (no sub-sentence splitting).
        Assert.All(chunks, c => Assert.True(c.Text.Length <= opts.MaxChunkSize));
    }

    [Fact]
    public void UseSemanticChunking_RegistersStrategyAndOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddRagNet(rag => rag.UseSemanticChunking());

        var provider = services.BuildServiceProvider();
        var strategy = provider.GetService<IChunkingStrategy>();
        var options = provider.GetService<SemanticChunkingOptions>();

        Assert.IsType<SemanticChunkingStrategy>(strategy);
        Assert.NotNull(options);
    }
}
