using Microsoft.Extensions.AI;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class FaithfulnessEvaluatorTests
{
    private static FaithfulnessEvaluator Evaluator(IChatClient client)
        => new(new RagasJudge(client, new RagasOptions()));

    private static EvaluationSample Sample()
        => new("What is X?", "The predicted answer.", "The reference answer.", ["retrieved context"]);

    [Fact]
    public async Task ScoreAsync_WhenClaimExtractionFails_IsNotScoreable()
    {
        // Pre-3.1 this returned 1.0 — a malformed reply scored better than a real answer.
        var client = new RoutingChatClient([("Extract", "sorry, I cannot")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    [Fact]
    public async Task ScoreAsync_WhenTheModelFencesItsJson_IsNotScoreable()
    {
        // A markdown fence is the most common real-world malformation, and the one the pre-3.1
        // tests recorded as "returns one gracefully".
        var client = new RoutingChatClient([("Extract", "```json\n[\"a claim\"]\n```")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    [Fact]
    public async Task ScoreAsync_AnswerAssertsNothing_IsTriviallyFaithful()
    {
        // Distinguishable from a parse failure now, which is the whole point.
        var client = new RoutingChatClient([("Extract", "[]")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_AllClaimsSupported_ScoresOne()
    {
        var client = new RoutingChatClient([("Extract", """["alpha","beta"]""")], fallback: "yes");

        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_HalfTheClaimsSupported_ScoresAHalf()
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
            ("alpha", "yes"),
            ("beta", "I think so?"),
        ]);

        // One readable verdict, and it was "yes" -> 1.0 over the judgements actually obtained.
        var score = await Evaluator(client).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
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
            new EvaluationSample("q", "a", "r", SourceChunks: null),
            TestContext.Current.CancellationToken);

        // Nothing retrieved is an absence of evidence, not evidence of an ungrounded answer.
        Assert.Null(score);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_EmptySourceChunks_IsNotScoreable()
    {
        var client = new RoutingChatClient([]);

        var score = await Evaluator(client).ScoreAsync(
            new EvaluationSample("q", "a", "r", SourceChunks: []),
            TestContext.Current.CancellationToken);

        Assert.Null(score);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_VerifyPromptAsksForExactlyWhatTheParserAccepts()
    {
        // A prompt that invites prose produces verdicts the strict parser rejects, which
        // silently shrinks the denominator.
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
        var evaluator = new FaithfulnessEvaluator(client);

        var score = await evaluator.ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
        Assert.False(evaluator.RequiresGroundTruth);
    }
}
