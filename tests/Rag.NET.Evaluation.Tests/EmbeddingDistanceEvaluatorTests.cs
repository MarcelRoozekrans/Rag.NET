using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class EmbeddingDistanceEvaluatorTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(
        float[][] firstCallVectors,
        float[][] secondCallVectors)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        var firstResult = new GeneratedEmbeddings<Embedding<float>>(
            firstCallVectors.Select(v => new Embedding<float>(v)).ToArray());

        var secondResult = new GeneratedEmbeddings<Embedding<float>>(
            secondCallVectors.Select(v => new Embedding<float>(v)).ToArray());

        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(firstResult, secondResult);

        return embedder;
    }

    [Fact]
    public async Task EvaluateAsync_NullSamples_ThrowsArgumentNullException()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var evaluator = new EmbeddingDistanceEvaluator(embedder);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => evaluator.EvaluateAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvaluateAsync_EmptySamples_ThrowsArgumentException()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var evaluator = new EmbeddingDistanceEvaluator(embedder);

        await Assert.ThrowsAsync<ArgumentException>(
            () => evaluator.EvaluateAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvaluateAsync_IdenticalEmbeddings_ScoreIsOne()
    {
        var vector = new float[] { 1f, 0f, 0f };
        var embedder = MakeEmbedder(
            firstCallVectors: [vector],
            secondCallVectors: [vector]);

        var evaluator = new EmbeddingDistanceEvaluator(embedder);
        var samples = new[]
        {
            new EvaluationSample("q", "predicted", "reference"),
        };

        var result = await evaluator.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var singleScore1 = Assert.Single(result.Scores);
        Assert.Equal(1.0, singleScore1, precision: 10);
        Assert.Equal(1.0, result.MeanScore, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_OrthogonalEmbeddings_ScoreIsZero()
    {
        var embedder = MakeEmbedder(
            firstCallVectors: [[1f, 0f, 0f]],
            secondCallVectors: [[0f, 1f, 0f]]);

        var evaluator = new EmbeddingDistanceEvaluator(embedder);
        var samples = new[]
        {
            new EvaluationSample("q", "predicted", "reference"),
        };

        var result = await evaluator.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var singleScore2 = Assert.Single(result.Scores);
        Assert.Equal(0.0, singleScore2, precision: 10);
        Assert.Equal(0.0, result.MeanScore, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_MultipleSamples_MeanScoreIsAverage()
    {
        // First call returns predicted embeddings: [1,0,0] for sample 0, [1,0,0] for sample 1
        // Second call returns reference embeddings: [1,0,0] for sample 0 (identical → score=1.0),
        //                                           [0,1,0] for sample 1 (orthogonal → score=0.0)
        // Mean = (1.0 + 0.0) / 2 = 0.5
        var embedder = MakeEmbedder(
            firstCallVectors: [[1f, 0f, 0f], [1f, 0f, 0f]],
            secondCallVectors: [[1f, 0f, 0f], [0f, 1f, 0f]]);

        var evaluator = new EmbeddingDistanceEvaluator(embedder);
        var samples = new[]
        {
            new EvaluationSample("q1", "predicted1", "reference1"),
            new EvaluationSample("q2", "predicted2", "reference2"),
        };

        var result = await evaluator.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Scores.Count);
        Assert.Equal(1.0, result.Scores[0], precision: 10);
        Assert.Equal(0.0, result.Scores[1], precision: 10);
        Assert.Equal(0.5, result.MeanScore, precision: 10);
    }
}
