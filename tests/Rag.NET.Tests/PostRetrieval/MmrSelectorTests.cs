using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class MmrSelectorTests
{
    private static SearchResult MakeResult(string text, double score = 0.9) => new()
    {
        Chunk = new TextChunk { Text = text, DocumentId = "doc-1", ChunkIndex = 0 },
        Score = score,
    };

    /// <summary>
    /// Sets up an embedder that returns a specific vector for each text.
    /// allTexts and allVectors must be parallel arrays covering every text that will be embedded
    /// (query + all chunk texts). The embedder matches by the text value, not by call order.
    /// </summary>
    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(
        string[] allTexts, float[][] allVectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var embeddings = texts.Select(t =>
                {
                    var idx = Array.IndexOf(allTexts, t);
                    return new Embedding<float>(allVectors[idx]);
                }).ToList();
                return new GeneratedEmbeddings<Embedding<float>>(embeddings);
            });
        return embedder;
    }

    [Fact]
    public async Task SelectAsync_EmptyCandidates_ReturnsEmpty()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var result = await MmrSelector.SelectAsync(
            "query", [], embedder, topK: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result);
        await embedder.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectAsync_TopKExceedsCandidates_ReturnsAll()
    {
        var candidates = new[] { MakeResult("only") };
        var embedder = MakeEmbedder(
            ["query", "only"],
            [new float[] { 1f, 0f }, new float[] { 1f, 0f }]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("only", result[0].Chunk.Text);
    }

    [Fact]
    public async Task SelectAsync_Lambda1_SelectsByRelevanceOnly()
    {
        // lambda=1.0 → pure relevance, ignore diversity.
        // Query=[1,0]. A=[1,0] (sim=1.0), B=[0,1] (sim=0.0). TopK=1 → select A.
        var candidates = new[]
        {
            MakeResult("A"),
            MakeResult("B"),
        };
        var embedder = MakeEmbedder(
            ["query", "A", "B"],
            [new float[] { 1f, 0f }, new float[] { 1f, 0f }, new float[] { 0f, 1f }]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 1, lambda: 1.0f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("A", result[0].Chunk.Text);
    }

    [Fact]
    public async Task SelectAsync_Lambda0_SelectsByDiversityOnly()
    {
        // lambda=0.0 → pure diversity.
        // After selecting A (first pick), B is orthogonal (most diverse), C is identical to A.
        // TopK=2 → [A, B].
        var candidates = new[]
        {
            MakeResult("A"),
            MakeResult("B"),
            MakeResult("C"),
        };
        var embedder = MakeEmbedder(
            ["query", "A", "B", "C"],
            [
                new float[] { 1f, 0f },
                new float[] { 1f, 0f },   // A
                new float[] { 0f, 1f },   // B — orthogonal to A
                new float[] { 1f, 0f },   // C — identical to A
            ]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 2, lambda: 0.0f,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Chunk.Text);
        Assert.Equal("B", result[1].Chunk.Text);
    }

    [Fact]
    public async Task SelectAsync_DefaultLambda_BalancesRelevanceAndDiversity()
    {
        // lambda=0.5. Query=[1,0].
        // A=[1,0] (sim_q=1.0) — selected first.
        // After A is selected: B=[0,1] (sim_q=0.0, sim_A=0.0), C=[0.9,√0.19] (sim_q≈0.9, sim_A≈0.9).
        // MMR(B)=0.5*0.0-0.5*0.0=0.0, MMR(C)=0.5*0.9-0.5*0.9=0.0. Tied → B wins (first in list).
        // TopK=2 → [A, B].
        var candidates = new[]
        {
            MakeResult("A"),
            MakeResult("B"),
            MakeResult("C"),
        };
        var embedder = MakeEmbedder(
            ["query", "A", "B", "C"],
            [
                new float[] { 1f, 0f },
                new float[] { 1f, 0f },
                new float[] { 0f, 1f },
                new float[] { 0.9f, (float)Math.Sqrt(1 - 0.81) },
            ]);

        var result = await MmrSelector.SelectAsync(
            "query", candidates, embedder, topK: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Chunk.Text);
        Assert.Equal("B", result[1].Chunk.Text);
    }
}
