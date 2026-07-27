using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class FaithfulnessEvaluatorTests
{
    private static EvaluationSample MakeSample(string[] chunks) =>
        new("What is X?", "X is Y and Z.", "X is Y.", chunks);

    [Fact]
    public async Task ScoreAsync_AllClaimsSupported_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        // First call: extract claims → ["X is Y", "X is Z"]
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[\"X is Y\",\"X is Z\"]")),
                // Second call: verify "X is Y" → yes
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                // Third call: verify "X is Z" → yes
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new FaithfulnessEvaluator(client);
        var score = await evaluator.ScoreAsync(MakeSample(["X is Y. X is Z."]), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_NoClaimsSupported_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[\"hallucinated claim\"]")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "no")));

        var evaluator = new FaithfulnessEvaluator(client);
        var score = await evaluator.ScoreAsync(MakeSample(["Unrelated context."]), TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_EmptySourceChunks_IsNotScoreable()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new FaithfulnessEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", SourceChunks: []);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        // Nothing retrieved is an absence of evidence, not evidence of an ungrounded answer.
        // This returned 0.0 before Phase 3.1, conflating the two.
        Assert.Null(score);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_MalformedClaimsJson_IsNotScoreable()
    {
        // LLM answers in prose instead of the JSON array it was asked for
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I'm sorry, I can't do that.")));

        var evaluator = new FaithfulnessEvaluator(client);
        var sample = new EvaluationSample("Q?", "Answer.", "Ref.", ["Context."]);

        // Before Phase 3.1 the JsonException became an empty claim list and scored 1.0 — the
        // worst reply produced the best score. It is now reported as unscoreable.
        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Null(score);
        // Only 1 LLM call (claims extraction) — no verification calls because nothing parsed
        await client.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_MarkdownFencedClaimsJson_IsScored()
    {
        // LLM wraps JSON in a markdown fence — common real-world response, and a wrapping rather
        // than a malformation. Excluding every fenced sample would make the metric report null
        // against a fence-happy model, so the fence is stripped and the reply underneath scored.
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "```json\n[\"a claim\"]\n```")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new FaithfulnessEvaluator(client);
        var sample = new EvaluationSample("Q?", "Answer.", "Ref.", ["Context."]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 2);
        await client.Received(2).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
