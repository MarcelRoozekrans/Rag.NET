using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class ContextPrecisionEvaluatorTests
{
    [Fact]
    public async Task ScoreAsync_AllChunksRelevant_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["Chunk1", "Chunk2"]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_HalfChunksRelevant_ReturnsHalf()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "no")));

        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["ChunkA", "ChunkB"]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.5, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_EmptyReferenceAnswer_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", string.Empty, ["Chunk1"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken));

        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreAsync_NullSourceChunks_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.");

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
