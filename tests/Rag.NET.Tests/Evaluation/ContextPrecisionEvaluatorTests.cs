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

        Assert.Equal(1.0, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_RelevantChunkRankedSecond_ReturnsHalf()
    {
        // Routed on chunk text rather than call order: the relevance calls now run concurrently,
        // so a sequence of canned replies would not reliably land on the chunk it was meant for.
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join('\n', callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Text));
                var verdict = prompt.Contains("ChunkB", StringComparison.Ordinal) ? "yes" : "no";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, verdict)));
            });

        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["ChunkA", "ChunkB"]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        // Average precision: the single relevant chunk sits at rank 2, so P@2 = 1/2.
        Assert.Equal(0.5, score!.Value, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_RelevantChunkRankedFirst_ScoresHigherThanRankedLast()
    {
        // The definitional fix: relevant/total scored these two identically.
        var score = await ScoreWithGoldAtAsync(relevantChunk: "ChunkA");
        var worse = await ScoreWithGoldAtAsync(relevantChunk: "ChunkC");

        Assert.True(score > worse, $"rank-blind: first={score}, last={worse}");
        Assert.Equal(1.0, score!.Value, precision: 2);
        Assert.Equal(1.0 / 3.0, worse!.Value, precision: 2);
    }

    private static Task<double?> ScoreWithGoldAtAsync(string relevantChunk)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join('\n', callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Text));
                var verdict = prompt.Contains($"Context Chunk: {relevantChunk}", StringComparison.Ordinal) ? "yes" : "no";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, verdict)));
            });

        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["ChunkA", "ChunkB", "ChunkC"]);

        return evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);
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
    public async Task ScoreAsync_NullSourceChunks_IsNotScoreable()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.");

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        // Nothing retrieved cannot be ranked well or badly; this returned 0.0 before Phase 3.1.
        Assert.Null(score);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
