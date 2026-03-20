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
        Assert.Equal(0.90, judgement.Criteria["faithfulness"].Score, precision: 10);
        Assert.Equal(1.00, judgement.Criteria["relevance"].Score, precision: 10);
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

    [Fact]
    public async Task EvaluateAsync_WithoutSources_FaithfulnessAbsentFromResult()
    {
        var client = MakeChatClient(ValidJsonAllCriteria);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "What is RAG?",
                PredictedAnswer: "RAG is retrieval-augmented generation.",
                ReferenceAnswer: "RAG combines retrieval with LLM generation.",
                SourceChunks: null),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.False(judgement.Criteria.ContainsKey("faithfulness"));
        Assert.True(judgement.Criteria.ContainsKey("correctness"));
        Assert.True(judgement.Criteria.ContainsKey("relevance"));
    }

    [Fact]
    public async Task EvaluateAsync_WithoutSources_PromptDoesNotMentionFaithfulness()
    {
        var capturedMessages = new List<IEnumerable<ChatMessage>>();
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages.Add(msgs)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJsonTwoCriteria)]));

        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "What is RAG?",
                PredictedAnswer: "RAG is retrieval-augmented generation.",
                ReferenceAnswer: "RAG combines retrieval with LLM generation.",
                SourceChunks: null),
        };

        await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var captured = Assert.Single(capturedMessages);
        foreach (var message in captured)
            Assert.DoesNotContain("faithfulness", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_MalformedJson_ThrowsLlmJudgeException()
    {
        const string malformedJson = "this is not json at all {{{";
        var client = MakeChatClient(malformedJson);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample("Q?", "A.", "R."),
        };

        var ex = await Assert.ThrowsAsync<LlmJudgeException>(
            () => sut.EvaluateAsync(samples, TestContext.Current.CancellationToken));

        Assert.Equal(malformedJson, ex.RawResponse);
    }

    [Fact]
    public async Task EvaluateAsync_MarkdownFencedJson_ParsedSuccessfully()
    {
        const string fencedJson = """
            ```json
            {
              "correctness": { "score": 0.80, "reasoning": "Good." },
              "relevance":   { "score": 0.90, "reasoning": "Relevant." }
            }
            ```
            """;

        var client = MakeChatClient(fencedJson);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "What is RAG?",
                PredictedAnswer: "RAG is retrieval-augmented generation.",
                ReferenceAnswer: "RAG combines retrieval with LLM generation.",
                SourceChunks: null),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.True(judgement.Criteria.ContainsKey("correctness"));
        Assert.True(judgement.Criteria.ContainsKey("relevance"));
        Assert.Equal(0.80, judgement.Criteria["correctness"].Score, precision: 10);
        Assert.Equal(0.90, judgement.Criteria["relevance"].Score, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_MissingCriterionKey_ThrowsLlmJudgeException()
    {
        // JSON with only "relevance" — "correctness" is missing
        const string jsonMissingCorrectness = """
            {
              "relevance": { "score": 0.95, "reasoning": "Relevant." }
            }
            """;

        var client = MakeChatClient(jsonMissingCorrectness);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "Q?",
                PredictedAnswer: "A.",
                ReferenceAnswer: "R.",
                SourceChunks: null),
        };

        var ex = await Assert.ThrowsAsync<LlmJudgeException>(
            () => sut.EvaluateAsync(samples, TestContext.Current.CancellationToken));

        Assert.Contains("correctness", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(ex.RawResponse);
    }

    [Fact]
    public async Task EvaluateAsync_ScoreAboveOne_IsClamped()
    {
        const string jsonScoreAboveOne = """
            {
              "correctness": { "score": 1.5, "reasoning": "too high" },
              "relevance":   { "score": 0.8, "reasoning": "ok" }
            }
            """;

        var client = MakeChatClient(jsonScoreAboveOne);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "Q?",
                PredictedAnswer: "A.",
                ReferenceAnswer: "R.",
                SourceChunks: null),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.Equal(1.0, judgement.Criteria["correctness"].Score, precision: 10);
        Assert.Equal(0.8, judgement.Criteria["relevance"].Score, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_ScoreBelowZero_IsClamped()
    {
        const string jsonScoreBelowZero = """
            {
              "correctness": { "score": 0.7, "reasoning": "ok" },
              "relevance":   { "score": -0.5, "reasoning": "too low" }
            }
            """;

        var client = MakeChatClient(jsonScoreBelowZero);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "Q?",
                PredictedAnswer: "A.",
                ReferenceAnswer: "R.",
                SourceChunks: null),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.Equal(0.0, judgement.Criteria["relevance"].Score, precision: 10);
        Assert.Equal(0.7, judgement.Criteria["correctness"].Score, precision: 10);
    }

    [Fact]
    public async Task EvaluateAsync_WithEmptySourceChunks_FaithfulnessAbsentFromResult()
    {
        var client = MakeChatClient(ValidJsonAllCriteria);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "What is RAG?",
                PredictedAnswer: "RAG is retrieval-augmented generation.",
                ReferenceAnswer: "RAG combines retrieval with LLM generation.",
                SourceChunks: []),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.False(judgement.Criteria.ContainsKey("faithfulness"));
        Assert.True(judgement.Criteria.ContainsKey("correctness"));
        Assert.True(judgement.Criteria.ContainsKey("relevance"));
    }

    [Fact]
    public async Task EvaluateAsync_EmptySamples_ThrowsArgumentException()
    {
        var client = MakeChatClient(ValidJsonTwoCriteria);
        var sut = new LlmJudgeEvaluator(client);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.EvaluateAsync(Array.Empty<EvaluationSample>(), TestContext.Current.CancellationToken));

        Assert.Equal("samples", ex.ParamName);
    }
}
