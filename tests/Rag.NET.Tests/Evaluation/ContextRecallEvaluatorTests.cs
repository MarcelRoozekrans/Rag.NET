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

        Assert.Equal(1.0, score, precision: 2);
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

        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_NullSourceChunks_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.");

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score, precision: 2);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
