using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class AnswerRelevanceEvaluatorTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(float[] vector)
    {
        var gen = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        gen.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                    embeddings.Add(new Embedding<float>(vector));
                return Task.FromResult(embeddings);
            });
        return gen;
    }

    [Fact]
    public async Task ScoreAsync_IdenticalEmbeddings_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        // Returns 3 synthetic questions (one per call)
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q1?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q2?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q3?")));

        // All embeddings identical → cosine = 1.0
        var embedder = MakeEmbedder([1f, 0f, 0f]);
        var evaluator = new AnswerRelevanceEvaluator(client, embedder);
        var sample = new EvaluationSample("What is X?", "X is Y.", string.Empty);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_OrthogonalEmbeddings_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q1?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q2?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q3?")));

        var totalEmbeddings = 0;
        var gen = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        gen.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                {
                    // First embedding = original question [1,0]; rest = synthetic questions [0,1] (orthogonal)
                    embeddings.Add(totalEmbeddings++ == 0
                        ? new Embedding<float>(new float[] { 1f, 0f })
                        : new Embedding<float>(new float[] { 0f, 1f }));
                }
                return Task.FromResult(embeddings);
            });

        var evaluator = new AnswerRelevanceEvaluator(client, gen);
        var sample = new EvaluationSample("What is X?", "X is Y.", string.Empty);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score!.Value, precision: 2);
    }
}
