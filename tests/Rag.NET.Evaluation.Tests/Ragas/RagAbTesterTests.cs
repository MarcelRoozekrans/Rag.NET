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
            Suite((Quality, s => IsB(s) ? BaseScore(s) + Lift(s) : BaseScore(s))),
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
    public async Task CompareAsync_SameSeedOverTheSameDeltas_GivesTheSameInterval()
    {
        var report1 = await Compare(new AbOptions { Seed = 11, BootstrapResamples = 500 });
        var report2 = await Compare(new AbOptions { Seed = 11, BootstrapResamples = 500 });

        // An unreproducible confidence interval is not evidence.
        var first = report1.Metrics[Quality].ConfidenceInterval!.Value;
        var second = report2.Metrics[Quality].ConfidenceInterval!.Value;
        Assert.Equal(first.Lower, second.Lower, precision: 12);
        Assert.Equal(first.Upper, second.Upper, precision: 12);

        static Task<AbReport> Compare(AbOptions options) => new RagAbTester(
            Suite((Quality, s => IsB(s) ? BaseScore(s) + Lift(s) : BaseScore(s))),
            options).CompareAsync(
                new AbVariant("A", new ScriptedPipeline("A")),
                new AbVariant("B", new ScriptedPipeline("B")),
                Samples("q1", "q2", "q3", "q4", "q5", "q6"),
                TestContext.Current.CancellationToken);
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
    private sealed class ScriptedPipeline(string label, string? throwOn = null) : IRagPipeline
    {
        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
        {
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
