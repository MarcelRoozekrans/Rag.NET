namespace Rag.NET.Evaluation.Internal;

/// <summary>
/// The paired-comparison arithmetic behind the A/B harness, as pure functions.
/// </summary>
/// <remarks>
/// <para>
/// Separated from execution so the statistics can be pinned by table tests without a pipeline and
/// without a model. This is the part of the phase where a wrong answer would be least visible: an
/// A/B run always produces a higher number on one side, and only the interval says whether that
/// number means anything.
/// </para>
/// <para>
/// The convention throughout is <c>delta = b - a</c>, so a positive delta favours variant B.
/// </para>
/// </remarks>
internal static class AbStatistics
{
    /// <summary>
    /// The share of the bootstrap distribution trimmed from each tail, giving a 95% interval.
    /// </summary>
    private const double TailProbability = 0.025;

    /// <summary>
    /// Per-sample <c>b - a</c> for every index where <b>both</b> sides scored.
    /// </summary>
    /// <param name="a">Variant A's per-sample scores, <c>null</c> where the sample was unscoreable.</param>
    /// <param name="b">Variant B's per-sample scores, aligned index-for-index with <paramref name="a"/>.</param>
    /// <returns>The kept deltas, in input order. Empty when nothing was comparable.</returns>
    /// <exception cref="ArgumentException">The two sides have different lengths, so they cannot be paired.</exception>
    /// <remarks>
    /// A pair is all-or-nothing. Keeping the readable half of a pair would compute the two means
    /// over different sample sets while still describing the result as paired.
    /// </remarks>
    public static double[] PairedDeltas(IReadOnlyList<double?> a, IReadOnlyList<double?> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Count != b.Count)
        {
            throw new ArgumentException(
                $"Paired scores must be the same length; A has {a.Count} and B has {b.Count}.",
                nameof(b));
        }

        // Sized to the upper bound and trimmed once, so the common case — nothing dropped —
        // allocates exactly one array. Indexed rather than foreach: no enumerator over the
        // interface, and nothing in the loop for ZA0601 to object to.
        var deltas = new double[a.Count];
        var kept = 0;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] is { } left && b[i] is { } right)
                deltas[kept++] = right - left;
        }

        return kept == deltas.Length ? deltas : deltas[..kept];
    }

    /// <summary>Mean of the paired deltas; <c>null</c> when there are none.</summary>
    /// <param name="deltas">The paired deltas, as returned by <see cref="PairedDeltas"/>.</param>
    /// <returns>The arithmetic mean, or <c>null</c> when <paramref name="deltas"/> is empty.</returns>
    /// <remarks>
    /// Nullable rather than <c>0.0</c>: a mean delta of zero says the variants tied, which is a
    /// result. <c>null</c> says nothing was comparable, which is not. Collapsing the two is the
    /// defect this milestone keeps finding — a fabricated value is indistinguishable from a
    /// measured one once it is in the report.
    /// </remarks>
    public static double? MeanDelta(ReadOnlySpan<double> deltas)
    {
        if (deltas.IsEmpty)
            return null;

        // `ref readonly` rather than a copy: the span enumerator hands out references, and HLQ004
        // objects to silently copying each element out of one.
        var sum = 0.0;
        foreach (ref readonly var delta in deltas)
            sum += delta;

        return sum / deltas.Length;
    }

    /// <summary>How often each variant won, counting anything within <paramref name="epsilon"/> as a tie.</summary>
    /// <param name="deltas">The paired deltas, as returned by <see cref="PairedDeltas"/>.</param>
    /// <param name="epsilon">
    /// The half-width of the tie band. Deltas in <c>[-epsilon, +epsilon]</c> are ties, which keeps
    /// floating-point noise on two identically scored samples out of the win column.
    /// </param>
    /// <returns>The win/loss/tie counts, summing to the length of <paramref name="deltas"/>.</returns>
    public static AbTally Tally(ReadOnlySpan<double> deltas, double epsilon)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epsilon);

        var bWins = 0;
        var aWins = 0;
        var ties = 0;

        foreach (ref readonly var delta in deltas)
        {
            if (delta > epsilon)
                bWins++;
            else if (delta < -epsilon)
                aWins++;
            else
                ties++;
        }

        return new AbTally(bWins, aWins, ties);
    }

    /// <summary>
    /// A 95% percentile-bootstrap confidence interval on the mean delta; <c>null</c> when there are
    /// no pairs.
    /// </summary>
    /// <param name="deltas">The paired deltas, as returned by <see cref="PairedDeltas"/>.</param>
    /// <param name="resamples">
    /// How many resamples to draw. More resamples reduce the Monte-Carlo jitter of the interval
    /// itself; they do not narrow it, because the width is set by the data.
    /// </param>
    /// <param name="random">
    /// The randomness. Seed it (<c>new Random(seed)</c>) and the same deltas yield the same
    /// interval; that reproducibility is the whole point of taking it as a parameter, exactly as
    /// for <c>ReservoirSampler</c> and the dataset seed.
    /// </param>
    /// <returns>The interval, or <c>null</c> when <paramref name="deltas"/> is empty.</returns>
    /// <remarks>
    /// <para>
    /// Bootstrap rather than a t-interval because RAGAS scores are bounded on <c>[0, 1]</c> and
    /// frequently skewed — a metric where most samples score 0.9+ is nowhere near normal, and a
    /// t-interval would overstate its own precision. The bootstrap assumes only exchangeability.
    /// </para>
    /// <para>
    /// <b>Percentile convention.</b> The resample means are sorted and the same number of values is
    /// trimmed from each tail: <c>trim = floor(0.025 * resamples)</c>, giving
    /// <c>[means[trim], means[resamples - 1 - trim]]</c>. Several conventions are defensible — this
    /// one is chosen because it is symmetric by construction, so neither variant is favoured by the
    /// rounding, and because it degrades sensibly: a single pair makes every resample mean identical
    /// and the interval collapses to that point rather than throwing or inventing a width.
    /// </para>
    /// </remarks>
    public static AbConfidenceInterval? BootstrapMeanDeltaCi(
        ReadOnlySpan<double> deltas,
        int resamples,
        Random random)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resamples);
        ArgumentNullException.ThrowIfNull(random);

        if (deltas.IsEmpty)
            return null;

        // One buffer for the whole run, filled in place through a `ref` enumerator so the outer
        // loop writes without indexing (HLQ013). The inner draw stays indexed: it is random access,
        // which no enumerator expresses, and ZA0601 rules out a LINQ mean.
        var means = new double[resamples];
        foreach (ref var mean in means.AsSpan())
        {
            var sum = 0.0;
            for (var i = 0; i < deltas.Length; i++)
                sum += deltas[random.Next(deltas.Length)];

            mean = sum / deltas.Length;
        }

        Array.Sort(means);

        var trim = (int)Math.Floor(TailProbability * resamples);
        return new AbConfidenceInterval(means[trim], means[resamples - 1 - trim]);
    }
}
