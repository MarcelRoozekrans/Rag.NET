using Microsoft.Extensions.AI;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class ContextRecallEvaluatorTests
{
    private static ContextRecallEvaluator Evaluator(IChatClient client)
        => new(new RagasJudge(client, new RagasOptions()));

    private static EvaluationSample Sample()
        => new("What is X?", "The predicted answer.", "The reference answer.", ["retrieved context"]);

    [Fact]
    public async Task ScoreAsync_WhenStatementExtractionFails_IsNotScoreable()
    {
        // Pre-3.1 this returned 1.0 — perfect recall claimed from an unreadable reply.
        var client = new RoutingChatClient([("Extract", "not valid json at all")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    [Fact]
    public async Task ScoreAsync_ReferenceAssertsNothing_IsTriviallyRecalled()
    {
        var client = new RoutingChatClient([("Extract", "[]")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_AllStatementsSupported_ScoresOne()
    {
        var client = new RoutingChatClient([("Extract", """["alpha","beta"]""")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_HalfTheStatementsSupported_ScoresAHalf()
    {
        var client = new RoutingChatClient(
        [
            ("Extract", """["alpha","beta"]"""),
            ("alpha", "yes"),
            ("beta", "no"),
        ]);

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(0.5, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_UnparseableVerdictsAreExcludedNotCountedAgainst()
    {
        var client = new RoutingChatClient(
        [
            ("Extract", """["alpha","beta"]"""),
            ("alpha", "no"),
            ("beta", "it depends"),
        ]);

        // One readable verdict, and it was "no" -> 0.0 over the judgements actually obtained.
        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_NoVerdictReadable_IsNotScoreable()
    {
        var client = new RoutingChatClient([("Extract", """["alpha"]""")], fallback: "possibly");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    [Fact]
    public async Task ScoreAsync_NoSourceChunks_IsNotScoreableAndCostsNothing()
    {
        var client = new RoutingChatClient([]);

        var score = await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "reference"),
            TestContext.Current.CancellationToken);

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
    public async Task ScoreAsync_VerifyPromptAsksForExactlyWhatTheParserAccepts()
    {
        var client = new RoutingChatClient([("Extract", """["alpha"]""")], fallback: "yes");

        await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Contains(
            client.Prompts,
            prompt => prompt.Contains("""Respond with exactly "yes" or "no" and nothing else.""", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScoreAsync_ViaThePublicChatClientConstructor_StillWorksStandalone()
    {
        var client = new RoutingChatClient([("Extract", "[]")], fallback: "yes");
        var evaluator = new ContextRecallEvaluator(client);

        var score = await evaluator.ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
        Assert.True(evaluator.RequiresGroundTruth);
    }
}
