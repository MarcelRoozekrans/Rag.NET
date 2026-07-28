using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>
/// The tester composes a runner, a scorer and the statistics, so what is worth pinning here is the
/// composition: that identical variants produce no winner, that a real improvement survives the
/// interval, and that every dropped pair is counted under the right heading.
/// </summary>
/// <remarks>
/// Scored by a scripted metric rather than a model. A judge would make these tests measure the
/// judge, and the point of each one is a rule the tester is supposed to enforce.
/// </remarks>
public sealed class RagAbTesterTests
{
    private const string Quality = "Quality";
    private const string Alpha = "Alpha";
    private const string Beta = "Beta";

    [Fact]
    public async Task CompareAsync_IdenticalVariants_DoNotProduceAWinner()
    {
        // The same pipeline on both sides, scored by a metric that varies with the question but not
        // with the variant. Every delta is therefore exactly zero.
        //
        // This is the test the whole design exists for. Without it the framework rubber-stamps
        // whatever was tried last: an A/B run always produces a higher number on one side, so a
        // tester that reports the mean delta without an honest interval names a winner every time,
        // including when there is nothing to win.
        var pipeline = new ScriptedPipeline("same");
        var tester = new RagAbTester(
            Suite((Quality, BaseScore)),
            new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", pipeline),
            new AbVariant("B", pipeline),
            Samples("q1", "q2", "q3", "q4", "q5", "q6", "q7", "q8"),
            TestContext.Current.CancellationToken);

        var quality = report.Metrics[Quality];
        Assert.Equal(0.0, quality.MeanDelta!.Value, precision: 10);

        var ci = quality.ConfidenceInterval!.Value;
        Assert.True(
            ci.Lower <= 0 && ci.Upper >= 0,
            $"CI [{ci.Lower}, {ci.Upper}] must span zero when the variants are identical");

        Assert.Equal(0, quality.Tally.BWins);
        Assert.Equal(0, quality.Tally.AWins);
        Assert.Equal(8, quality.Tally.Ties);
    }

    [Fact]
    public async Task CompareAsync_WhenBIsUniformlyBetter_ReportsAPositiveDeltaAndAnIntervalExcludingZero()
    {
        var tester = new RagAbTester(
            Suite((Quality, Lifted)),
            new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B")),
            Samples("q1", "q2", "q3", "q4", "q5", "q6", "q7", "q8"),
            TestContext.Current.CancellationToken);

        var quality = report.Metrics[Quality];
        Assert.Equal(0.20, quality.MeanDelta!.Value, precision: 10);

        // The lift is between +0.15 and +0.25 on every sample, so no resample of it can reach zero.
        var ci = quality.ConfidenceInterval!.Value;
        Assert.True(ci.Lower > 0, $"CI [{ci.Lower}, {ci.Upper}] should exclude zero for a real shift");

        Assert.Equal(8, quality.Tally.BWins);
        Assert.Equal(0, quality.Tally.AWins);
        Assert.Equal(0, quality.Tally.Ties);
        Assert.Equal(8, quality.ComparedPairs);
    }

    [Fact]
    public async Task CompareAsync_WhenAMetricCannotScoreOneSample_DropsThatPairFromThatMetricOnly()
    {
        // Alpha cannot read B's answer to q2. Beta reads everything.
        var tester = new RagAbTester(
            Suite(
                (Alpha, s => IsB(s) && Asks(s, "q2") ? null : BaseScore(s)),
                (Beta, BaseScore)),
            new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B")),
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        var alpha = report.Metrics[Alpha];
        Assert.Equal(2, alpha.ComparedPairs);
        Assert.Equal(1, alpha.DroppedAsUnscoreable);
        Assert.Equal(0, alpha.DroppedForRunFailure);

        // An unscoreable sample is a fact about one metric, not about the run: Beta read the same
        // answer without trouble and must not lose a pair to Alpha's failure.
        var beta = report.Metrics[Beta];
        Assert.Equal(3, beta.ComparedPairs);
        Assert.Equal(0, beta.DroppedAsUnscoreable);
        Assert.Equal(3, report.ComparableSamples);
        Assert.Equal(0, report.RunFailures);
    }

    [Fact]
    public async Task CompareAsync_WhenAVariantThrowsOnOneSample_DropsItFromEveryMetric()
    {
        var tester = new RagAbTester(
            Suite((Alpha, BaseScore), (Beta, BaseScore)),
            new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B", throwOn: "q2")),
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, report.SamplesRun);
        Assert.Equal(2, report.ComparableSamples);
        Assert.Equal(1, report.RunFailures);

        // A sample one variant never answered has no B-side answer to score, so it leaves every
        // metric — and it is counted under run failure rather than folded in with the metric's own
        // unscoreable pairs, because the two have different causes and different fixes.
        foreach (var metric in new[] { report.Metrics[Alpha], report.Metrics[Beta] })
        {
            Assert.Equal(2, metric.ComparedPairs);
            Assert.Equal(1, metric.DroppedForRunFailure);
            Assert.Equal(0, metric.DroppedAsUnscoreable);
        }

        var failure = Assert.Single(report.Failures);
        Assert.Contains("q2", failure, StringComparison.Ordinal);
        Assert.Contains("B", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_WhenNoPairIsScoreable_ReportsNullsRatherThanZeros()
    {
        // Alpha never reads B. Beta reads both sides, so the run itself was fine — which is exactly
        // the case where a fabricated 0.0 for Alpha would look like a measured tie.
        var tester = new RagAbTester(
            Suite((Alpha, s => IsB(s) ? null : BaseScore(s)), (Beta, BaseScore)),
            new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B")),
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        var alpha = report.Metrics[Alpha];
        Assert.Null(alpha.MeanDelta);
        Assert.Null(alpha.ConfidenceInterval);
        Assert.Null(alpha.MeanA);
        Assert.Null(alpha.MeanB);
        Assert.Equal(0, alpha.ComparedPairs);

        // And the count says why it is null, which is the difference between "nothing was
        // comparable" and "the variants tied".
        Assert.Equal(3, alpha.DroppedAsUnscoreable);
        Assert.NotNull(report.Metrics[Beta].MeanDelta);
    }

    [Fact]
    public async Task CompareAsync_WhenEveryRunFails_ReportsNoMetricsAndSaysHowManyWereLost()
    {
        var tester = new RagAbTester(Suite((Quality, BaseScore)), new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B", throwOn: "*")),
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        // No metric rows at all: the metric names come from the suite's report and the suite was
        // never run, so inventing a row of nulls would claim it ran and found nothing.
        Assert.Empty(report.Metrics);
        Assert.Equal(0, report.ComparableSamples);
        Assert.Equal(3, report.RunFailures);
        Assert.Equal(3, report.Failures.Count);

        Assert.Null(report.Latency.MeanDeltaMilliseconds);
        Assert.Null(report.Latency.ConfidenceIntervalMilliseconds);
        Assert.Null(report.Latency.MedianA);
        Assert.Equal(0, report.Latency.ComparedPairs);
    }

    [Fact]
    public async Task CompareAsync_ScoresEachVariantWithItsOwnAnswerAndItsOwnSources()
    {
        // RagResponse exposes Sources, not SourceChunks; EvaluationSample's parameter is the one
        // called SourceChunks. Getting that mapping wrong would score both variants against the
        // dataset's fields and report the pipelines as identical however different they are.
        var seen = new List<EvaluationSample>();
        var tester = new RagAbTester(
            Suite((Quality, s => { seen.Add(s); return BaseScore(s); })),
            new AbOptions { Seed = 7 });

        await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B")),
            Samples("q1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, seen.Count);
        Assert.Equal("A:q1", seen[0].PredictedAnswer);
        Assert.Equal("B:q1", seen[1].PredictedAnswer);
        Assert.Equal("reference for q1", seen[0].ReferenceAnswer);
        Assert.Equal(["A context for q1"], seen[0].SourceChunks);
        Assert.Equal(["B context for q1"], seen[1].SourceChunks);
    }

    [Fact]
    public async Task CompareAsync_MeansAreTakenOverTheComparedPairsSoTheyAgreeWithTheDelta()
    {
        // q2 is dropped for Alpha. If each side's mean were taken over everything it happened to
        // score, MeanB - MeanA would disagree with MeanDelta in the same report.
        var tester = new RagAbTester(
            Suite((Alpha, s => IsB(s) && Asks(s, "q2") ? null : BaseScore(s) + (IsB(s) ? Lift(s) : 0.0))),
            new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B")),
            Samples("q1", "q2", "q3", "q4"),
            TestContext.Current.CancellationToken);

        var alpha = report.Metrics[Alpha];
        Assert.Equal(3, alpha.ComparedPairs);
        Assert.Equal(alpha.MeanDelta!.Value, alpha.MeanB!.Value - alpha.MeanA!.Value, precision: 10);
    }

    [Fact]
    public async Task CompareAsync_WithoutALedger_ReportsCostAsAbsentRatherThanZero()
    {
        var tester = new RagAbTester(Suite((Quality, BaseScore)), new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B"), CostLedger: new ScriptedLedger(2m, 5m)),
            Samples("q1", "q2"),
            TestContext.Current.CancellationToken);

        // Nothing measured A's spend. A zero would claim it was free.
        Assert.False(report.Cost.ContainsKey("A"));
        Assert.Equal(3m, report.Cost["B"]);
    }

    [Fact]
    public async Task CompareAsync_WhenTheLedgerReadsLowerAfterTheRun_ReportsThatVariantAsAbsent()
    {
        // The spend window is the current UTC calendar day. A comparison is two pipelines over the
        // whole dataset plus a judge pass, so a run that starts in the evening and finishes after
        // midnight reads a bucket that emptied underneath it: 5 before, 2 after.
        var tester = new RagAbTester(Suite((Quality, BaseScore)), new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A"), CostLedger: new ScriptedLedger(5m, 2m)),
            new AbVariant("B", new ScriptedPipeline("B"), CostLedger: new ScriptedLedger(2m, 5m)),
            Samples("q1", "q2"),
            TestContext.Current.CancellationToken);

        // -3 is not an imprecise cost, it is an impossible one. Absent says "not measured", which is
        // exactly what a rolled-over window leaves behind — and is the same signal a missing ledger
        // gives, so a reader needs no new rule to interpret it.
        Assert.False(report.Cost.ContainsKey("A"));

        // The other variant's ledger did not roll over and is still reported.
        Assert.Equal(3m, report.Cost["B"]);
    }

    [Fact]
    public async Task CompareAsync_SameSeedOverTheSameDeltas_GivesTheSameInterval()
    {
        var report1 = await Compare(new AbOptions { Seed = 11, BootstrapResamples = 1000 });
        var report2 = await Compare(new AbOptions { Seed = 11, BootstrapResamples = 1000 });

        // An unreproducible confidence interval is not evidence.
        var first = report1.Metrics[Quality].ConfidenceInterval!.Value;
        var second = report2.Metrics[Quality].ConfidenceInterval!.Value;
        Assert.Equal(first.Lower, second.Lower, precision: 12);
        Assert.Equal(first.Upper, second.Upper, precision: 12);

        static Task<AbReport> Compare(AbOptions options) => new RagAbTester(
            Suite((Quality, Lifted)),
            options).CompareAsync(
                new AbVariant("A", new ScriptedPipeline("A")),
                new AbVariant("B", new ScriptedPipeline("B")),
                Samples("q1", "q2", "q3", "q4", "q5", "q6"),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompareAsync_TheIntervalDependsOnItsOwnDeltasOnly_NotOnTheRestOfTheSuite()
    {
        // Quality's deltas are identical in all three suites; only the company it keeps differs.
        // One Random shared across the intervals would make Quality's interval a function of how
        // many metrics were drawn before it — reproducible, but only for an identical suite, which
        // is a weaker promise than Seed makes and one nobody would think to check before comparing
        // two reports. A fresh generator per interval is what makes the seed mean what it says.
        //
        // Note which suites are needed to see this. Registration order alone cannot: the metric
        // names are sorted before the intervals are drawn, so `alphaFirst` and `qualityFirst` draw
        // in the same order either way and a shared generator survives them. `alone` is the one
        // that discriminates, because there Quality is drawn first rather than second.
        var alone = await Compare(Suite((Quality, GradedLift)));
        var alphaFirst = await Compare(Suite((Alpha, BaseScore), (Quality, GradedLift)));
        var qualityFirst = await Compare(Suite((Quality, GradedLift), (Alpha, BaseScore)));

        var expected = alone.Metrics[Quality].ConfidenceInterval!.Value;
        foreach (var report in new[] { alphaFirst, qualityFirst })
        {
            var actual = report.Metrics[Quality].ConfidenceInterval!.Value;
            Assert.Equal(expected.Lower, actual.Lower, precision: 12);
            Assert.Equal(expected.Upper, actual.Upper, precision: 12);
        }

        static Task<AbReport> Compare(RagasEvaluationSuite suite) => new RagAbTester(
            suite,
            new AbOptions { Seed = 11, BootstrapResamples = 1000 }).CompareAsync(
                new AbVariant("A", new ScriptedPipeline("A")),
                new AbVariant("B", new ScriptedPipeline("B")),
                Samples("q1", "q2", "q3", "q4", "q5", "q6", "q7", "q8", "q9"),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompareAsync_WhenTheTokenIsCancelledMidRun_StopsRatherThanReportingFailures()
    {
        using var cancellation = new CancellationTokenSource();
        var asked = new List<string>();
        var scored = new List<EvaluationSample>();

        // Cancels while the first sample is being answered. That is what makes this discriminating:
        // a token merely checked on the way in would let all three samples run.
        void Ask(string question)
        {
            asked.Add(question);
            cancellation.Cancel();
        }

        var tester = new RagAbTester(
            Suite((Quality, s => { scored.Add(s); return BaseScore(s); })),
            new AbOptions { Seed = 7 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A", onAsk: Ask)),
            new AbVariant("B", new ScriptedPipeline("B", onAsk: Ask)),
            Samples("q1", "q2", "q3"),
            cancellation.Token));

        // Only the first sample's two calls happened, and the judge never ran. Cancellation is the
        // caller asking to stop, so it propagates — swallowing it would report a stopped run as a
        // comparison in which every remaining sample failed, and would keep paying for the judge.
        Assert.Equal(["q1", "q1"], asked);
        Assert.Empty(scored);
    }

    [Fact]
    public async Task CompareAsync_RecordsLatencyOverTheComparablePairsOnly()
    {
        var tester = new RagAbTester(Suite((Quality, BaseScore)), new AbOptions { Seed = 7 });

        var report = await tester.CompareAsync(
            new AbVariant("A", new ScriptedPipeline("A")),
            new AbVariant("B", new ScriptedPipeline("B", throwOn: "q2")),
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        // Both variants' percentiles have to come from the same set of questions, or they compare
        // two different workloads.
        Assert.Equal(2, report.Latency.ComparedPairs);
        Assert.NotNull(report.Latency.MedianA);
        Assert.NotNull(report.Latency.MedianB);
        Assert.NotNull(report.Latency.MeanDeltaMilliseconds);
    }

    [Fact]
    public async Task CompareAsync_NullVariant_Throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new RagAbTester(Suite((Quality, BaseScore))).CompareAsync(
                null!,
                new AbVariant("B", new ScriptedPipeline("B")),
                Samples("q1"),
                TestContext.Current.CancellationToken));

    private static double? BaseScore(EvaluationSample sample) => 0.30 + ((sample.Question[^1] - '0') * 0.05);

    private static double Lift(EvaluationSample sample) => (sample.Question[^1] - '0') % 2 == 0 ? 0.15 : 0.25;

    /// <summary>The base score, plus B's parity lift of +0.15 or +0.25.</summary>
    private static double? Lifted(EvaluationSample sample)
        => IsB(sample) ? BaseScore(sample) + Lift(sample) : BaseScore(sample);

    /// <summary>The base score, plus a lift for B that is <b>different on every question</b>.</summary>
    /// <remarks>
    /// <para>
    /// For the tests that are about the resampling itself rather than about the delta. Any test that
    /// compares two intervals needs endpoints that can actually move: a bootstrap endpoint is an
    /// order statistic over the reachable resample means, so if the deltas are coarse it lands on
    /// the same reachable value whatever was drawn, and the test passes against a generator it was
    /// written to catch.
    /// </para>
    /// <para>
    /// <see cref="Lift"/> is exactly that coarse — two values, hence seven reachable means over six
    /// pairs. These lifts are the fractional parts of multiples of the golden ratio, so no two
    /// multisets of nine draws share a sum and the endpoints track the draws continuously.
    /// </para>
    /// </remarks>
    private static double? GradedLift(EvaluationSample sample)
    {
        var lift = 0.2 * ((sample.Question[^1] - '0') * 0.6180339887498949 % 1.0);

        return IsB(sample) ? BaseScore(sample) + lift : BaseScore(sample);
    }

    private static bool Asks(EvaluationSample sample, string question)
        => string.Equals(sample.Question, question, StringComparison.Ordinal);

    private static bool IsB(EvaluationSample sample)
        => sample.PredictedAnswer.StartsWith("B:", StringComparison.Ordinal);

    private static RagasEvaluationSuite Suite(
        params (string Name, Func<EvaluationSample, double?> Score)[] metrics)
    {
        var registered = new List<(string Name, IRagasMetric Metric)>(metrics.Length);
        foreach (var (name, score) in metrics)
            registered.Add((name, new ScriptedMetric(score)));

        return new RagasEvaluationSuite(registered);
    }

    private static EvaluationSample[] Samples(params string[] questions)
    {
        var samples = new EvaluationSample[questions.Length];
        for (var i = 0; i < questions.Length; i++)
            samples[i] = new EvaluationSample(questions[i], PredictedAnswer: "", $"reference for {questions[i]}");

        return samples;
    }

    /// <summary>A metric whose score is decided by the test rather than by a model.</summary>
    private sealed class ScriptedMetric(Func<EvaluationSample, double?> score) : IRagasMetric
    {
        public bool RequiresGroundTruth => false;

        public Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
            => Task.FromResult(score(sample));
    }

    /// <summary>
    /// A pipeline that answers <c>"{label}:{question}"</c> and retrieves one chunk saying so.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than substituted because the projection assertions need the answer and
    /// the sources to identify which variant produced them.
    /// </remarks>
    /// <param name="label">Prefix for the answer and the retrieved chunk, so the variant is identifiable.</param>
    /// <param name="throwOn">The question to fail on, or <c>"*"</c> for every question.</param>
    /// <param name="onAsk">
    /// Called with every question before it is answered. The hook the cancellation test uses to
    /// cancel from inside the run, which is the only way to observe that the token reaches the loop
    /// rather than only the entry point.
    /// </param>
    private sealed class ScriptedPipeline(
        string label,
        string? throwOn = null,
        Action<string>? onAsk = null) : IRagPipeline
    {
        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            onAsk?.Invoke(query);

            if (throwOn is not null && (string.Equals(throwOn, "*", StringComparison.Ordinal) || string.Equals(throwOn, query, StringComparison.Ordinal)))
                return Task.FromException<RagResponse>(new InvalidOperationException("the vector store went away"));

            return Task.FromResult(new RagResponse
            {
                Answer = $"{label}:{query}",
                Sources =
                [
                    new SearchResult
                    {
                        Chunk = new TextChunk
                        {
                            Text = $"{label} context for {query}",
                            DocumentId = new DocumentId("doc"),
                            ChunkIndex = 0,
                        },
                        Score = 1.0,
                    },
                ],
            });
        }

        public Task<Result<IngestionResult, RagError>> IngestAsync(
            Stream document,
            DocumentMetadata metadata,
            IngestionOptions? options = null,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>A ledger that reports a scripted spend on each read, so a before/after delta is exact.</summary>
    private sealed class ScriptedLedger(params decimal[] spends) : ICostLedger
    {
        private int _reads;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
            => Task.FromResult(spends[Math.Min(_reads++, spends.Length - 1)]);
    }
}
