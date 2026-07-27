using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class ContextRecallEvaluatorTests
{
    [Fact]
    public async Task ScoreAsync_AllStatementsSupported_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                // Extract statements → 2 statements
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[\"Stmt1\",\"Stmt2\"]")),
                // Stmt1 supported
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                // Stmt2 supported
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref1. Ref2.", ["Chunk covering both."]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_EmptyReferenceAnswer_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", string.Empty, ["Chunk1"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken));

        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_NoStatementsExtracted_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Short.", ["Chunk."]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_NullSourceChunks_IsNotScoreable()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.");

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        // Returned 0.0 before Phase 3.1, which claimed the retrieval missed everything.
        Assert.Null(score);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_MalformedStatementsJson_IsNotScoreable()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid json at all")));

        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["Chunk."]);

        // Before Phase 3.1 the JsonException became an empty statement list and scored a perfect
        // 1.0. An unreadable reply is now reported as unscoreable.
        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Null(score);
        await client.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
