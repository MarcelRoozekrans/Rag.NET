using Rag.NET.Evaluation.Internal;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Runs one evaluation dataset through two pipeline configurations and reports which is better,
/// with an interval that says whether "better" means anything.
/// </summary>
/// <remarks>
/// <para>
/// The offline half of the A/B framework. Both variants answer the same questions, so the
/// comparison is paired: <c>delta_i = B_i - A_i</c> per sample. Pairing removes between-sample
/// variance — some questions are simply harder than others — which is what makes a fifty-sample
/// comparison worth running at all.
/// </para>
/// <para>
/// <b>Why it lives in the RAGAS package.</b> The pairing needs a per-sample score for each metric,
/// and <see cref="RagasReport.Samples"/> is the only thing in the evaluation stack that produces
/// one; <c>IRagEvaluator</c> returns an aggregate with no metric breakdown. Since
/// <c>Rag.NET.Evaluation.Ragas</c> references <c>Rag.NET.Evaluation</c> and not the other way round,
/// the composition root has to sit on this side of that edge. Everything it produces —
/// <see cref="AbReport"/>, <see cref="AbOptions"/>, <see cref="AbVariant"/> — stays in
/// <c>Rag.NET.Evaluation</c>, so the report model owes nothing to RAGAS and a later scorer can
/// reuse it.
/// </para>
/// <para>
/// <b>What it drops, and why it says so.</b> A sample either variant failed to answer is excluded
/// from every metric, because there is no B-side answer to score. A sample a metric could not score
/// on either side is excluded from that metric only. Both are counted and reported: a clean delta
/// over the ten pairs that survived out of fifty is not a result about the dataset, and the counts
/// are the only place that is visible.
/// </para>
/// <para>
/// Shadow mode — running a secondary against live traffic — is not here. It is a production-path
/// concern with its own failure modes and is scheduled as Phase 3.8.
/// </para>
/// </remarks>
public sealed class RagAbTester
{
    private readonly RagasEvaluationSuite _suite;
    private readonly AbOptions _options;

    /// <summary>Creates a tester that scores both variants with the same suite.</summary>
    /// <param name="suite">
    /// The metrics both variants are scored with. One suite rather than one per variant, so the two
    /// sides are judged by the same model with the same prompts — a comparison in which the metrics
    /// differ measures the metrics.
    /// </param>
    /// <param name="options">Bootstrap and tie-band tuning; defaults are used when omitted.</param>
    public RagAbTester(RagasEvaluationSuite suite, AbOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(suite);

        _suite = suite;
        _options = options ?? new AbOptions();
    }

    /// <summary>Runs both variants over the dataset and reports the paired comparison.</summary>
    /// <param name="a">The baseline. Its scores are the <c>A</c> side of every delta.</param>
    /// <param name="b">The challenger. Deltas are <c>B - A</c>, so a positive delta favours this one.</param>
    /// <param name="samples">
    /// The dataset. Only <c>Question</c> and <c>ReferenceAnswer</c> are read — each variant supplies
    /// its own predicted answer and its own sources, which is the whole point.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    /// <returns>The comparison, including how many pairs were dropped and why.</returns>
    /// <exception cref="ArgumentException">Both variants have the same name, or one has no name.</exception>
    /// <remarks>
    /// Sequential, alternating which variant leads each sample. Running them concurrently would
    /// halve the wall-clock and make the latency numbers measure contention for one provider and one
    /// connection pool as much as they measure the variants.
    /// </remarks>
    public async Task<AbReport> CompareAsync(
        AbVariant a,
        AbVariant b,
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(samples);

        var spendBefore = await SpendAsync(a, b, cancellationToken).ConfigureAwait(false);
        var runs = await AbRunner.RunAsync([a, b], samples, cancellationToken).ConfigureAwait(false);

        // Closed here rather than after the scoring pass: the reported figure is what each variant's
        // pipeline spent, and the judge is one shared suite whose spend belongs to neither side.
        var spendAfter = await SpendAsync(a, b, cancellationToken).ConfigureAwait(false);
        var comparable = Comparable(runs);

        var metrics = comparable.Length == 0
            ? new Dictionary<string, AbMetricComparison>(StringComparer.Ordinal)
            : await CompareMetricsAsync(a, b, comparable, runs.Count, cancellationToken).ConfigureAwait(false);

        return new AbReport
        {
            VariantA = a.Name,
            VariantB = b.Name,
            SamplesRun = runs.Count,
            ComparableSamples = comparable.Length,
            RunFailures = runs.Count - comparable.Length,
            Failures = Failures(runs),
            Metrics = metrics,
            Latency = CompareLatency(comparable, a.Name, b.Name),
            Cost = CostDelta(a, b, spendBefore, spendAfter),
        };
    }

    /// <summary>Scores both variants over the comparable samples and pairs the results per metric.</summary>
    /// <remarks>
    /// The two suite runs are sequential rather than concurrent: they share one judge and therefore
    /// one concurrency ceiling, and overlapping them would put twice the caller's stated limit in
    /// flight.
    /// </remarks>
    private async Task<Dictionary<string, AbMetricComparison>> CompareMetricsAsync(
        AbVariant a,
        AbVariant b,
        VariantRun[] comparable,
        int samplesRun,
        CancellationToken cancellationToken)
    {
        var reportA = await _suite.EvaluateAsync(Project(comparable, a.Name), cancellationToken).ConfigureAwait(false);
        var reportB = await _suite.EvaluateAsync(Project(comparable, b.Name), cancellationToken).ConfigureAwait(false);

        var runFailures = samplesRun - comparable.Length;
        var names = MetricNames(reportA);
        var comparisons = new Dictionary<string, AbMetricComparison>(names.Length, StringComparer.Ordinal);

        foreach (var name in names)
            comparisons[name] = CompareMetric(name, reportA, reportB, runFailures);

        return comparisons;
    }

    /// <summary>Pairs one metric's per-sample scores and runs the statistics over the survivors.</summary>
    private AbMetricComparison CompareMetric(
        string metric,
        RagasReport reportA,
        RagasReport reportB,
        int runFailures)
    {
        // Index i of one report pairs with index i of the other: both were evaluated over the same
        // projection of the same comparable runs, and RagasEvaluationSuite appends one entry per
        // sample as it walks the input sequentially.
        var count = reportA.Samples.Count;
        var scoresA = new double?[count];
        var scoresB = new double?[count];

        for (var i = 0; i < count; i++)
        {
            scoresA[i] = Score(reportA.Samples[i], metric);
            scoresB[i] = Score(reportB.Samples[i], metric);
        }

        var deltas = AbStatistics.PairedDeltas(scoresA, scoresB);

        return new AbMetricComparison
        {
            MeanA = PairedMean(scoresA, scoresB),
            MeanB = PairedMean(scoresB, scoresA),
            MeanDelta = AbStatistics.MeanDelta(deltas),
            ConfidenceInterval = AbStatistics.BootstrapMeanDeltaCi(deltas, _options.BootstrapResamples, NewRandom()),
            Tally = AbStatistics.Tally(deltas, _options.TieEpsilon),
            ComparedPairs = deltas.Length,
            DroppedForRunFailure = runFailures,
            DroppedAsUnscoreable = count - deltas.Length,
        };
    }

    /// <summary>Per-variant percentiles and the paired latency delta, over the comparable samples.</summary>
    private AbLatencyComparison CompareLatency(VariantRun[] comparable, string nameA, string nameB)
    {
        var latenciesA = new TimeSpan[comparable.Length];
        var latenciesB = new TimeSpan[comparable.Length];
        var deltas = new double[comparable.Length];

        for (var i = 0; i < comparable.Length; i++)
        {
            latenciesA[i] = comparable[i].Elapsed[nameA];
            latenciesB[i] = comparable[i].Elapsed[nameB];
            deltas[i] = (latenciesB[i] - latenciesA[i]).TotalMilliseconds;
        }

        // Percentiles need sorted copies; the deltas must stay in sample order until the bootstrap
        // resamples them, which is why they are built before the sort rather than from it.
        Array.Sort(latenciesA);
        Array.Sort(latenciesB);

        return new AbLatencyComparison
        {
            MedianA = AbStatistics.Percentile(latenciesA, 0.50),
            Percentile95A = AbStatistics.Percentile(latenciesA, 0.95),
            MedianB = AbStatistics.Percentile(latenciesB, 0.50),
            Percentile95B = AbStatistics.Percentile(latenciesB, 0.95),
            MeanDeltaMilliseconds = AbStatistics.MeanDelta(deltas),
            ConfidenceIntervalMilliseconds =
                AbStatistics.BootstrapMeanDeltaCi(deltas, _options.BootstrapResamples, NewRandom()),
            ComparedPairs = deltas.Length,
        };
    }

    /// <summary>
    /// A fresh generator per interval, so each interval depends only on its own deltas and the seed.
    /// </summary>
    /// <remarks>
    /// Sharing one generator across the metrics would make each metric's interval depend on how many
    /// metrics were registered before it — reproducible, but only for an identical suite, which is a
    /// weaker promise than the one <see cref="AbOptions.Seed"/> makes.
    /// </remarks>
    private Random NewRandom() => _options.Seed is { } seed ? new Random(seed) : new Random();

    /// <summary>Turns one variant's responses into samples the suite can score.</summary>
    /// <remarks>
    /// The question and the reference answer come from the dataset; the predicted answer and the
    /// sources come from the variant. <c>Chunk.Text</c> rather than <c>CompressedText</c>: the
    /// metrics judge the retrieved context, and a variant that compresses harder would otherwise be
    /// scored against a shorter context than it retrieved.
    /// </remarks>
    private static EvaluationSample[] Project(VariantRun[] runs, string variantName)
    {
        var samples = new EvaluationSample[runs.Length];
        for (var i = 0; i < runs.Length; i++)
        {
            var response = runs[i].Responses[variantName];
            samples[i] = new EvaluationSample(
                runs[i].Sample.Question,
                response.Answer,
                runs[i].Sample.ReferenceAnswer,
                SourceTexts(response));
        }

        return samples;
    }

    private static string[] SourceTexts(RagResponse response)
    {
        var sources = response.Sources;
        var texts = new string[sources.Count];
        for (var i = 0; i < sources.Count; i++)
            texts[i] = sources[i].Chunk.Text;

        return texts;
    }

    private static VariantRun[] Comparable(IReadOnlyList<VariantRun> runs)
    {
        var kept = new VariantRun[runs.Count];
        var count = 0;

        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].IsComparable)
                kept[count++] = runs[i];
        }

        return count == kept.Length ? kept : kept[..count];
    }

    private static string[] Failures(IReadOnlyList<VariantRun> runs)
    {
        var lines = new string[runs.Count];
        var count = 0;

        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].Failure is { } failure)
            {
                lines[count++] =
                    $"'{runs[i].Sample.Question}': {failure.VariantName} did not answer — {failure.Message}";
            }
        }

        return count == lines.Length ? lines : lines[..count];
    }

    /// <summary>
    /// The registered metric names, sorted so the report reads the same way on every run.
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="RagasReport.UnscoreableSamples"/> rather than from a sample's score
    /// dictionary: that one is seeded with every registered metric, so a metric that scored nothing
    /// at all still appears — as a row of nulls with its drop count, rather than vanishing from the
    /// report exactly when it went wrong.
    /// </remarks>
    private static string[] MetricNames(RagasReport report)
    {
        var names = new string[report.UnscoreableSamples.Count];
        var i = 0;
        foreach (var pair in report.UnscoreableSamples)
            names[i++] = pair.Key;

        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    private static double? Score(RagasSampleScore sample, string metric)
        => sample.Scores.TryGetValue(metric, out var score) ? score : null;

    /// <summary>Mean of <paramref name="side"/> over the indices where both sides scored.</summary>
    /// <remarks>
    /// Restricted to the compared pairs so that <c>MeanB - MeanA</c> equals the reported mean delta.
    /// Taking each side's mean over everything it happened to score would produce two means over
    /// different sample sets and a third number that agrees with neither.
    /// </remarks>
    private static double? PairedMean(double?[] side, double?[] other)
    {
        var sum = 0.0;
        var count = 0;

        for (var i = 0; i < side.Length; i++)
        {
            if (side[i] is { } value && other[i] is not null)
            {
                sum += value;
                count++;
            }
        }

        return count > 0 ? sum / count : null;
    }

    /// <summary>Reads each variant's dedicated ledger, or null where none was supplied.</summary>
    private static async Task<(decimal? A, decimal? B)> SpendAsync(
        AbVariant a,
        AbVariant b,
        CancellationToken cancellationToken)
    {
        var spendA = a.CostLedger is { } ledgerA
            ? await ledgerA.GetSpendAsync(CostWindow.Day, cancellationToken).ConfigureAwait(false)
            : (decimal?)null;

        var spendB = b.CostLedger is { } ledgerB
            ? await ledgerB.GetSpendAsync(CostWindow.Day, cancellationToken).ConfigureAwait(false)
            : (decimal?)null;

        return (spendA, spendB);
    }

    /// <summary>
    /// What each variant spent during the run. A variant with no ledger is absent, never zero.
    /// </summary>
    /// <remarks>
    /// A variant whose ledger read <b>lower</b> at the end than at the start is absent too. The
    /// window is the current UTC calendar day, and a run that crosses midnight empties the bucket
    /// underneath the pair of readings, so the difference stops being a spend at all — see
    /// <see cref="AbVariant.CostLedger"/>.
    /// </remarks>
    private static Dictionary<string, decimal> CostDelta(
        AbVariant a,
        AbVariant b,
        (decimal? A, decimal? B) before,
        (decimal? A, decimal? B) after)
    {
        var cost = new Dictionary<string, decimal>(2, StringComparer.Ordinal);

        Record(cost, a.Name, before.A, after.A);
        Record(cost, b.Name, before.B, after.B);

        return cost;

        static void Record(Dictionary<string, decimal> cost, string name, decimal? before, decimal? after)
        {
            // Absent rather than negative. A negative spend is not an imprecise number, it is an
            // impossible one, and absence already carries exactly the right meaning here: the same
            // "not measured" a missing ledger reports.
            if (before is { } start && after is { } end && end >= start)
                cost[name] = end - start;
        }
    }
}
