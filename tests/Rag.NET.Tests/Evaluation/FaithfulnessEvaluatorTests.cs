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
    public async Task ScoreAsync_EmptySourceChunks_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new FaithfulnessEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", SourceChunks: []);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_MalformedClaimsJson_ReturnsOneGracefully()
    {
        // LLM wraps JSON in a markdown fence — common real-world response
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "```json\n[\"a claim\"]\n```")));

        var evaluator = new FaithfulnessEvaluator(client);
        var sample = new EvaluationSample("Q?", "Answer.", "Ref.", ["Context."]);

        // Malformed JSON (markdown fence) → JsonException → claims = [] → score = 1.0
        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 2);
        // Only 1 LLM call (claims extraction) — no verification calls because claims = []
        await client.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
