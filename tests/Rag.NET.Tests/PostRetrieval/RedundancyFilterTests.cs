using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class RedundancyFilterTests
{
    private static SearchResult MakeResult(string text, double score = 0.9) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = "doc-1", ChunkIndex = 0 },
        Score = score,
    };

    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(
        IReadOnlyList<string> texts,
        IReadOnlyList<float[]> vectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(
            texts.Select((_, i) => new Embedding<float>(vectors[i])).ToList());

        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(embeddings);

        return embedder;
    }

    [Fact]
    public async Task FilterAsync_EmptyList_ReturnsEmpty()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var result = await RedundancyFilter.FilterAsync(
            [],
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FilterAsync_SingleResult_ReturnsSingleResult()
    {
        var results = new[] { MakeResult("only one") };
        var embedder = MakeEmbedder(["only one"], [new float[] { 1f, 0f }]);

        var filtered = await RedundancyFilter.FilterAsync(
            results,
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(filtered);
        Assert.Equal("only one", filtered[0].Chunk.Text);
    }

    [Fact]
    public async Task FilterAsync_IdenticalEmbeddings_DropsRedundant()
    {
        // Two results with identical vectors → cosine similarity = 1.0 ≥ 0.95 → second dropped
        var results = new[] { MakeResult("chunk A"), MakeResult("chunk B") };
        var vector = new float[] { 1f, 0f };
        var embedder = MakeEmbedder(["chunk A", "chunk B"], [vector, vector]);

        var filtered = await RedundancyFilter.FilterAsync(
            results,
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(filtered);
        Assert.Equal("chunk A", filtered[0].Chunk.Text);
    }

    [Fact]
    public async Task FilterAsync_OrthogonalEmbeddings_KeepsBoth()
    {
        // Two results with orthogonal vectors → cosine similarity = 0.0 < 0.95 → both kept
        var results = new[] { MakeResult("chunk A"), MakeResult("chunk B") };
        var embedder = MakeEmbedder(
            ["chunk A", "chunk B"],
            [new float[] { 1f, 0f }, new float[] { 0f, 1f }]);

        var filtered = await RedundancyFilter.FilterAsync(
            results,
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("chunk A", filtered[0].Chunk.Text);
        Assert.Equal("chunk B", filtered[1].Chunk.Text);
    }

    [Fact]
    public async Task FilterAsync_PreservesOrderOfAcceptedResults()
    {
        // a, b, c: b is redundant to a (identical vector), c is orthogonal → result is [a, c]
        var resultA = MakeResult("chunk A");
        var resultB = MakeResult("chunk B");
        var resultC = MakeResult("chunk C");
        var results = new[] { resultA, resultB, resultC };

        var vectorA = new float[] { 1f, 0f };
        var vectorB = new float[] { 1f, 0f }; // identical to A → redundant
        var vectorC = new float[] { 0f, 1f }; // orthogonal to A → kept

        var embedder = MakeEmbedder(
            ["chunk A", "chunk B", "chunk C"],
            [vectorA, vectorB, vectorC]);

        var filtered = await RedundancyFilter.FilterAsync(
            results,
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("chunk A", filtered[0].Chunk.Text);
        Assert.Equal("chunk C", filtered[1].Chunk.Text);
    }

    [Fact]
    public async Task FilterAsync_SimilarityExactlyAtThreshold_IsFiltered()
    {
        // Construct two vectors where cosine similarity = 0.95 exactly.
        // first = [1, 0, 0], second = [cos(θ), sin(θ), 0] where cos(θ) = 0.95
        // cosine similarity = dot product of unit vectors = 0.95 ≥ threshold → second dropped
        var first = new float[] { 1f, 0f, 0f };
        var second = new float[] { 0.95f, (float)Math.Sqrt(1 - 0.95 * 0.95), 0f };

        var results = new[] { MakeResult("chunk A"), MakeResult("chunk B") };
        var embedder = MakeEmbedder(
            ["chunk A", "chunk B"],
            [first, second]);

        var filtered = await RedundancyFilter.FilterAsync(
            results,
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(filtered);
        Assert.Equal("chunk A", filtered[0].Chunk.Text);
    }

    [Fact]
    public async Task FilterAsync_SimilarityJustBelowThreshold_IsKept()
    {
        // first = [1, 0, 0], second = [0.94, sin(θ), 0] where cos(θ) ≈ 0.94
        // cosine similarity ≈ 0.94 < 0.95 threshold → both kept
        var first = new float[] { 1f, 0f, 0f };
        var second = new float[] { 0.94f, (float)Math.Sqrt(1 - 0.94 * 0.94), 0f };

        var results = new[] { MakeResult("chunk A"), MakeResult("chunk B") };
        var embedder = MakeEmbedder(
            ["chunk A", "chunk B"],
            [first, second]);

        var filtered = await RedundancyFilter.FilterAsync(
            results,
            embedder,
            threshold: 0.95f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("chunk A", filtered[0].Chunk.Text);
        Assert.Equal("chunk B", filtered[1].Chunk.Text);
    }
}
