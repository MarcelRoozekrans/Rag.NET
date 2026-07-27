using Microsoft.Extensions.AI;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class ContextPrecisionEvaluatorTests
{
    private static ContextPrecisionEvaluator Evaluator(IChatClient client)
        => new(new RagasJudge(client, new RagasOptions()));

    /// <summary>Scores chunks where only a chunk containing GOLD is judged relevant.</summary>
    private static Task<double?> ScoreAsync(params string[] chunks)
    {
        var client = new RoutingChatClient([("GOLD", "yes")], fallback: "no");
        return Evaluator(client).ScoreAsync(
            new EvaluationSample("What is X?", "X is Y.", "X is Y.", chunks),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScoreAsync_IsRankAware()
    {
        // Same chunks, same relevance, different order. Pre-3.1 both returned 1/3.
        var goldFirst = await ScoreAsync("GOLD", "junk", "junk");
        var goldLast = await ScoreAsync("junk", "junk", "GOLD");

        Assert.True(goldFirst > goldLast, $"rank-blind: first={goldFirst}, last={goldLast}");
        Assert.Equal(1.0, goldFirst!.Value, precision: 10);
        Assert.Equal(1.0 / 3.0, goldLast!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_AllChunksRelevant_ScoresOne()
        => Assert.Equal(1.0, (await ScoreAsync("GOLD one", "GOLD two"))!.Value, precision: 10);

    [Fact]
    public async Task ScoreAsync_NoChunkRelevant_ScoresZero()
        => Assert.Equal(0.0, (await ScoreAsync("junk", "junk"))!.Value, precision: 10);

    [Fact]
    public async Task ScoreAsync_RelevantChunkSecond_ScoresAHalf()
    {
        // P@2 = 1/2, one relevant chunk -> 0.5. Plain relevant/total would also say 0.5 here,
        // which is why the three-chunk case above is the one that pins rank-awareness.
        var score = await ScoreAsync("junk", "GOLD");

        Assert.Equal(0.5, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_UnparseableVerdictLeavesTheRankingInsteadOfCountingAsIrrelevant()
    {
        var client = new RoutingChatClient([("GOLD", "yes"), ("noise", "who can say")], fallback: "no");

        // Verdicts are yes, unparseable. Dropping the unreadable one leaves [relevant] -> 1.0.
        // Counting it as irrelevant would also say 1.0, because nothing is ranked behind it;
        // the case below is the one where the two readings disagree.
        var score = await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "reference", ["GOLD", "noise"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_UnparseableVerdictLeavesTheRankingAndPromotesWhatFollows()
    {
        var client = new RoutingChatClient([("noise", "who can say"), ("GOLD", "yes")], fallback: "no");

        // Verdicts are unparseable, yes. The unjudged chunk leaves the ranking entirely, which
        // promotes GOLD to rank 1 -> 1.0. Keeping it as an irrelevant chunk would preserve GOLD's
        // original rank 2 -> 0.5, penalising the retriever for a judgement the model never gave.
        var score = await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "reference", ["noise", "GOLD"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_NoVerdictReadable_IsNotScoreable()
    {
        var client = new RoutingChatClient([], fallback: "who can say");

        var score = await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "reference", ["chunk"]),
            TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    [Fact]
    public async Task ScoreAsync_NoSourceChunks_IsNotScoreableAndCostsNothing()
    {
        var client = new RoutingChatClient([]);

        var score = await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "reference"),
            TestContext.Current.CancellationToken);

        // Returned 0.0 before Phase 3.1, conflating "nothing retrieved" with "all of it wrong".
        Assert.Null(score);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_EmptyReferenceAnswer_ThrowsBeforeSpendingAnything()
    {
        var client = new RoutingChatClient([]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Evaluator(client).ScoreAsync(
                new EvaluationSample("q", "a", string.Empty, ["chunk"]),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_RelevancePromptAsksForExactlyWhatTheParserAccepts()
    {
        var client = new RoutingChatClient([], fallback: "yes");

        await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "reference", ["chunk"]),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            client.Prompts,
            prompt => prompt.Contains("""Respond with exactly "yes" or "no" and nothing else.""", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScoreAsync_ViaThePublicChatClientConstructor_StillWorksStandalone()
    {
        var client = new RoutingChatClient([], fallback: "yes");
        var evaluator = new ContextPrecisionEvaluator(client);

        var score = await evaluator.ScoreAsync(
            new EvaluationSample("q", "a", "reference", ["chunk"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
        Assert.True(evaluator.RequiresGroundTruth);
    }
}
