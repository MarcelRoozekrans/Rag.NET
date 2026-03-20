using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class LlmJudgeEvaluatorTests
{
    private const string ValidJsonAllCriteria = """
        {
          "correctness":  { "score": 0.85, "reasoning": "Mostly correct." },
          "faithfulness": { "score": 0.90, "reasoning": "Grounded in context." },
          "relevance":    { "score": 1.00, "reasoning": "Directly answers." }
        }
        """;

    private const string ValidJsonTwoCriteria = """
        {
          "correctness": { "score": 0.75, "reasoning": "Partially correct." },
          "relevance":   { "score": 0.95, "reasoning": "Relevant." }
        }
        """;

    private static IChatClient MakeChatClient(string jsonResponse)
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)]));
        return client;
    }

    [Fact]
    public async Task EvaluateAsync_WithSources_ReturnsAllThreeCriteria()
    {
        var client = MakeChatClient(ValidJsonAllCriteria);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "What is RAG?",
                PredictedAnswer: "RAG is retrieval-augmented generation.",
                ReferenceAnswer: "RAG combines retrieval with LLM generation.",
                SourceChunks: ["Context chunk 1", "Context chunk 2"]),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.Equal("What is RAG?", judgement.Question);
        Assert.True(judgement.Criteria.ContainsKey("correctness"));
        Assert.True(judgement.Criteria.ContainsKey("faithfulness"));
        Assert.True(judgement.Criteria.ContainsKey("relevance"));
        Assert.Equal(0.85, judgement.Criteria["correctness"].Score, precision: 10);
        Assert.Equal("Mostly correct.", judgement.Criteria["correctness"].Reasoning);
    }

    [Fact]
    public async Task EvaluateAsync_MultipleSamples_ReturnsOneJudgementPerSample()
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJsonTwoCriteria)]),
                new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJsonTwoCriteria)]));

        var sut = new LlmJudgeEvaluator(client);
        var samples = new[]
        {
            new EvaluationSample("Q1", "A1", "R1"),
            new EvaluationSample("Q2", "A2", "R2"),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Samples.Count);
    }
}
