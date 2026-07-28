namespace Rag.NET.Evaluation;

/// <summary>The outcome of one A/B comparison: what each metric said, and how much it saw.</summary>
/// <remarks>
/// <para>
/// <b>A confidence interval spanning zero is not a win.</b> Every A/B run produces a higher number
/// on one side of every metric, so the mean delta and the tally on their own always name a winner.
/// <see cref="AbMetricComparison.ConfidenceInterval"/> is the only member that says whether that
/// name means anything.
/// </para>
/// <para>
/// The counts on this report and on each metric are not bookkeeping. A comparison that dropped
/// forty of fifty samples can still show a clean delta over the ten that survived, and
/// <see cref="ComparableSamples"/> beside <see cref="SamplesRun"/> is where that shows up.
/// </para>
/// </remarks>
public sealed record AbReport
{
    /// <summary>The name of the variant whose scores are the <c>A</c> side of every delta.</summary>
    public required string VariantA { get; init; }

    /// <summary>The name of the variant whose scores are the <c>B</c> side. Deltas are <c>B - A</c>, so positive favours B.</summary>
    public required string VariantB { get; init; }

    /// <summary>How many samples were put through both variants.</summary>
    public required int SamplesRun { get; init; }

    /// <summary>
    /// How many of those samples both variants answered, and were therefore eligible for scoring.
    /// </summary>
    /// <remarks>
    /// Only these samples are sent to the evaluation suite. A sample one variant never answered has
    /// no B-side answer to score, and scoring the surviving side would compute the two variants'
    /// means over different sample sets while still describing the result as paired.
    /// </remarks>
    public required int ComparableSamples { get; init; }

    /// <summary>
    /// How many samples were dropped because a variant threw or returned nothing.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="AbMetricComparison.DroppedAsUnscoreable"/>: this is a variant that
    /// did not answer, that one is a metric that could not read an answer it was given. Different
    /// causes, different fixes, so they are counted separately rather than as one "dropped" total.
    /// </remarks>
    public required int RunFailures { get; init; }

    /// <summary>
    /// One line per failed sample: which question, which variant, and what it said.
    /// </summary>
    /// <remarks>
    /// A count alone tells a reader that something broke but not what, and the run has usually
    /// finished and exited by the time anyone looks. Only the first failing variant per sample is
    /// listed; the pair is excluded either way.
    /// </remarks>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>
    /// The comparison for each metric the evaluation suite registered, keyed by metric name.
    /// </summary>
    /// <remarks>
    /// Empty when <see cref="ComparableSamples"/> is zero: the metric names come from the suite's
    /// own report, and with nothing to score there is no report to take them from. An empty
    /// dictionary beside a non-zero <see cref="RunFailures"/> is the honest reading of that — better
    /// than a row of null metrics implying the suite ran and found nothing.
    /// </remarks>
    public required IReadOnlyDictionary<string, AbMetricComparison> Metrics { get; init; }

    /// <summary>Per-variant wall-clock and the paired difference between them.</summary>
    public required AbLatencyComparison Latency { get; init; }

    /// <summary>
    /// Spend during the run, by variant name, for variants that supplied a dedicated cost ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A variant that supplied no ledger is <b>absent from this dictionary</b> rather than present
    /// with <c>0</c>. Nothing measured its spend; reporting zero would claim it was free.
    /// </para>
    /// <para>
    /// A figure that is here is the difference between the ledger's day-window spend before and
    /// after the run, so it includes anything else that wrote to that ledger on the same UTC day.
    /// See <see cref="AbVariant.CostLedger"/> for why the ledger has to be the variant's own.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, decimal> Cost { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
}
