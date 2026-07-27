using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class RagasEvaluationSuiteTests
{
    private static EvaluationSample MakeSample(string referenceAnswer = "Ref.") =>
        new("Q?", "A.", referenceAnswer, ["Chunk."]);

    private static IChatClient AlwaysYesClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));
        return client;
    }

    /// <summary>
    /// Replies by prompt content, because the registered metrics run concurrently per sample and
    /// a sequence of canned replies would not reliably land on the call it was written for.
    /// </summary>
    private static IChatClient RoutedClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join('\n', callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Text));
                var reply = prompt switch
                {
                    _ when prompt.Contains("evasive", StringComparison.Ordinal) => "no",
                    _ when prompt.Contains("different questions", StringComparison.Ordinal) => "[\"Q1?\",\"Q2?\",\"Q3?\"]",
                    _ => "[]", // faithfulness: no claims
                };
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
            });
        return client;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> IdentityEmbedder()
    {
        var gen = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        gen.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                    embeddings.Add(new Embedding<float>(new float[] { 1f, 0f }));
                return Task.FromResult(embeddings);
            });
        return gen;
    }

    [Fact]
    public async Task EvaluateAsync_SingleFaithfulnessMetric_ReturnsReport()
    {
        // FaithfulnessEvaluator: LLM returns "[]" (no claims) → score = 1.0
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddFaithfulness()
            .Build();

        var report = await suite.EvaluateAsync([MakeSample()], TestContext.Current.CancellationToken);

        Assert.NotNull(report.Faithfulness);
        Assert.Null(report.AnswerRelevance);
        Assert.Null(report.ContextPrecision);
        Assert.Null(report.ContextRecall);
        Assert.Equal(report.Faithfulness!.Value, report.OverallScore!.Value, precision: 2);
    }

    [Fact]
    public void Build_ContextPrecisionRegistered_DoesNotThrow()
    {
        // Build() itself doesn't throw — validation happens at EvaluateAsync time
        var client = Substitute.For<IChatClient>();
        var builder = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddContextPrecision();

        var suite = builder.Build();
        Assert.NotNull(suite);
    }

    [Fact]
    public async Task EvaluateAsync_ContextPrecisionWithEmptyReferenceAnswer_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddContextPrecision()
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            suite.EvaluateAsync([MakeSample(referenceAnswer: "")], TestContext.Current.CancellationToken));

        // Fail-fast: no LLM calls should have been made before the exception
        await client.DidNotReceive().GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_EmptySamples_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddFaithfulness()
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            suite.EvaluateAsync([], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvaluateAsync_NoMetricsRegistered_Throws()
    {
        var client = Substitute.For<IChatClient>();
        // Build a suite with no metrics by not calling any Add* method
        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder()).Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            suite.EvaluateAsync([MakeSample()], TestContext.Current.CancellationToken));

        Assert.Equal("No metrics are registered.", ex.Message);
    }

    [Fact]
    public async Task EvaluateAsync_OverallScoreIsMeanOfRegisteredMetrics()
    {
        // Faithfulness: LLM returns "[]" → no claims → score = 1.0
        // AnswerRelevance: not evasive, three synthetic questions, identical embeddings → 1.0
        // Both registered → OverallScore = (1.0 + 1.0) / 2 = 1.0
        var client = RoutedClient();

        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddFaithfulness()
            .AddAnswerRelevance()
            .Build();

        var report = await suite.EvaluateAsync([MakeSample()], TestContext.Current.CancellationToken);

        Assert.NotNull(report.Faithfulness);
        Assert.NotNull(report.AnswerRelevance);
        var expected = (report.Faithfulness!.Value + report.AnswerRelevance!.Value) / 2.0;
        Assert.Equal(expected, report.OverallScore!.Value, precision: 2);
    }
}
