using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Shadow;
using Rag.NET.Evaluation.Tests.Ragas;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// <see cref="ShadowReplay"/> fed to <see cref="RagAbTester.CompareAsync"/>: the promise Phase 3.8
/// makes — capture now, score offline later — kept end to end. The central test here supplies
/// reference answers at replay time and runs <b>all four</b> RAGAS metrics, including the two that
/// throw on an empty reference; that is the payoff of capturing over inline scoring, executable.
/// </summary>
public sealed class ShadowReplayScoringTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompareAsync_OverAReplay_ScoresEachSidesCapturedAnswerAgainstItsCapturedContexts()
    {
        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")]);
        var seen = new List<EvaluationSample>();

        var report = await Tester(seen).CompareAsync(
            replay.VariantA,
            replay.VariantB,
            replay.Samples,
            TestContext.Current.CancellationToken);

        // Two samples, two variants: four scored projections, each built from the captured answer
        // and captured context texts of its own side — nothing invented, nothing re-run.
        Assert.Equal(4, seen.Count);
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "primary answer to q1", StringComparison.Ordinal)
            && s.SourceChunks!.SequenceEqual(["primary context for q1"], StringComparer.Ordinal));
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "shadow answer to q2", StringComparison.Ordinal)
            && s.SourceChunks!.SequenceEqual(["shadow context for q2"], StringComparer.Ordinal));
        Assert.Equal(2, report.ComparableSamples);
        Assert.Equal("primary", report.VariantA);
        Assert.Equal("shadow", report.VariantB);
    }

    [Fact]
    public async Task CompareAsync_WithReferencesSuppliedAtReplayTime_RunsAllFourRagasMetrics()
    {
        // The design's central claim: captures carry no reference answer, so only the
        // reference-free metrics can score them inline — but stored pairs can be annotated later,
        // at which point Context Precision and Context Recall become available. Both of those
        // evaluators throw on an empty ReferenceAnswer, so this passing proves the references
        // really reached them.
        var references = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["q1"] = "The annotated truth for q1.",
            ["q2"] = "The annotated truth for q2.",
        };
        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")], references);
        var judge = FourMetricJudge();

        var report = await new RagAbTester(FourMetricSuite(judge), new AbOptions { Seed = 7 }).CompareAsync(
            replay.VariantA,
            replay.VariantB,
            replay.Samples,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ComparableSamples);
        Assert.Equal(
            ["AnswerRelevance", "ContextPrecision", "ContextRecall", "Faithfulness"],
            [.. report.Metrics.Keys.Order(StringComparer.Ordinal)]);

        foreach (var comparison in report.Metrics.Values)
        {
            Assert.Equal(2, comparison.ComparedPairs);
            Assert.NotNull(comparison.MeanA);
            Assert.NotNull(comparison.MeanB);
        }

        // The ground-truth metrics did real work with the supplied references, not a stub's nod:
        // Context Precision judged chunks against the reference, Context Recall decomposed it.
        Assert.Contains(judge.Prompts, p =>
            p.Contains("useful for answering", StringComparison.Ordinal)
            && p.Contains("The annotated truth for q1.", StringComparison.Ordinal));
        Assert.Contains(judge.Prompts, p =>
            p.Contains("statements from the reference answer", StringComparison.Ordinal)
            && p.Contains("The annotated truth for q2.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_WithoutReferences_TheGroundTruthMetricsRefuseExactlyAsDocumented()
    {
        // The counterpart that makes the previous test meaningful: same captures, same suite, no
        // references — and the run is refused before any judging, naming the missing field.
        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RagAbTester(FourMetricSuite(FourMetricJudge())).CompareAsync(
                replay.VariantA,
                replay.VariantB,
                replay.Samples,
                TestContext.Current.CancellationToken));

        Assert.Contains("ReferenceAnswer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_OverAReplay_ACapturedSecondaryFailureLandsUnderRunFailures()
    {
        // The secondary threw in production and the capture recorded it. Replayed, it must reach
        // the report's failures — silently skipping it, or replaying an empty answer, would bias
        // the comparison toward whatever the secondary handles well.
        var replay = ShadowReplay.From([Capture("q1"), CaptureWithFailedSecondary("q2")]);

        var report = await Tester([]).CompareAsync(
            replay.VariantA,
            replay.VariantB,
            replay.Samples,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.SamplesRun);
        Assert.Equal(1, report.ComparableSamples);
        Assert.Equal(1, report.RunFailures);

        var failure = Assert.Single(report.Failures);
        Assert.Contains("shadow", failure, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: the vector store went away", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_TheSameQuestionCapturedTwice_ReplaysEachCapturesOwnAnswers()
    {
        // Production asks the same question more than once, and above temperature zero gets a
        // different answer each time. Both captures are real pairs, so both replay — in capture
        // order — rather than the first capture answering for every repeat.
        var first = new ShadowCapture("q1", Answered("primary", "first"), Answered("shadow", "first"), Timestamp);
        var second = new ShadowCapture("q1", Answered("primary", "second"), Answered("shadow", "second"), Timestamp);
        var replay = ShadowReplay.From([first, second]);
        var seen = new List<EvaluationSample>();

        var report = await Tester(seen).CompareAsync(
            replay.VariantA,
            replay.VariantB,
            replay.Samples,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.ComparableSamples);
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "primary answer to first", StringComparison.Ordinal));
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "primary answer to second", StringComparison.Ordinal));
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "shadow answer to second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_RunTwiceOverOneReplay_ReplaysBothTimes()
    {
        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")]);

        var first = await Tester([]).CompareAsync(
            replay.VariantA, replay.VariantB, replay.Samples, TestContext.Current.CancellationToken);
        var second = await Tester([]).CompareAsync(
            replay.VariantA, replay.VariantB, replay.Samples, TestContext.Current.CancellationToken);

        Assert.Equal(2, first.ComparableSamples);
        Assert.Equal(2, second.ComparableSamples);
        Assert.Empty(second.Failures);
    }

    [Fact]
    public async Task CompareAsync_OverSamplesNotInCaptureOrder_FailsLoudlyRatherThanMisattributingAnswers()
    {
        // The replayed pipelines serve the captures in capture order, which is the order
        // ShadowReplay.Samples walks them in. A caller who reorders or filters the samples gets
        // failure lines naming the mismatch — never a quietly wrong pairing scored as a result.
        var replay = ShadowReplay.From([Capture("q1"), Capture("q2")]);
        var reordered = new[] { replay.Samples[1], replay.Samples[0] };

        var report = await Tester([]).CompareAsync(
            replay.VariantA,
            replay.VariantB,
            reordered,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.RunFailures);
        Assert.Equal(0, report.ComparableSamples);
        Assert.All(report.Failures, line => Assert.Contains("capture order", line, StringComparison.Ordinal));
    }

    private static RagAbTester Tester(List<EvaluationSample> seen) => new(
        new RagasEvaluationSuite([("Quality", new ScriptedMetric(seen))]),
        new AbOptions { Seed = 7 });

    /// <summary>
    /// The four real RAGAS evaluators sharing one scripted judge. Only the LLM behind them is
    /// scripted; Context Precision and Context Recall run their genuine scoring paths, including
    /// their throw on an empty reference.
    /// </summary>
    private static RagasEvaluationSuite FourMetricSuite(RoutingChatClient judge)
        => new RagasEvaluationSuiteBuilder(judge, new StubEmbeddingGenerator([1f, 0f]))
            .AddFaithfulness()
            .AddAnswerRelevance()
            .AddContextPrecision()
            .AddContextRecall()
            .Build();

    private static RoutingChatClient FourMetricJudge() => new(
    [
        ("evasive", "no"),                                          // Answer Relevance: not a refusal
        ("different questions", """["Q one?","Q two?","Q three?"]"""), // Answer Relevance: generation
        ("atomic factual claims", """["a claim"]"""),               // Faithfulness: extraction
        ("atomic statements", """["a statement"]"""),               // Context Recall: extraction
    ], fallback: "yes");                                            // every verify/relevance verdict

    private static ShadowCapture Capture(string question) => new(
        question,
        Answered("primary", question),
        Answered("shadow", question),
        Timestamp);

    private static ShadowCapture CaptureWithFailedSecondary(string question) => new(
        question,
        Answered("primary", question),
        new ShadowVariantCapture(
            "shadow",
            Answer: null,
            ContextTexts: [],
            Failure: new ShadowVariantFailure("shadow", "InvalidOperationException: the vector store went away")),
        Timestamp);

    private static ShadowVariantCapture Answered(string name, string question) => new(
        name,
        Answer: $"{name} answer to {question}",
        ContextTexts: [$"{name} context for {question}"],
        Latency: TimeSpan.FromMilliseconds(100),
        Spend: 0.002m);

    /// <summary>A reference-free metric that records every projection it is asked to score.</summary>
    private sealed class ScriptedMetric(List<EvaluationSample> seen) : IRagasMetric
    {
        public bool RequiresGroundTruth => false;

        public Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
        {
            seen.Add(sample);
            return Task.FromResult<double?>(0.5);
        }
    }
}
