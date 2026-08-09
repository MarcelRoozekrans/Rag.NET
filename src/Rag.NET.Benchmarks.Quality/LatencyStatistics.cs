using System.Globalization;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Percentiles over one entrant's raw per-query latencies, computed here rather than by the
/// entrant — the reason <see cref="EntrantTimings"/> carries samples and not statistics. Five
/// entrants each reporting their own p99 would put five estimators into one column, and the
/// differences between estimators on a few hundred samples are the same size as the differences a
/// cost table would be claiming between libraries.
/// <para>
/// <b>The definition, pinned: nearest rank, no interpolation.</b> Sort the <c>n</c> samples
/// ascending and report the one at 1-based index <c>ceil(p / 100 x n)</c>. So p50 of
/// <c>[10, 20, 30, 40]</c> is <b>20</b>, the second of four — not the interpolated median 25,
/// which is a number no query ever took. Every value this reports is a latency some query actually
/// had, so a published cell can be found in the sidecar beside it.
/// </para>
/// <para>
/// <b>At small sample counts p99 is simply the maximum.</b> <c>ceil(0.99n) = n</c> for every
/// <c>n &lt; 100</c>, so below one hundred queries p99 is the single worst observation rather than
/// an estimate of a tail, and reading it as a tail figure means reading one query. At exactly
/// <c>n = 100</c> it becomes the 99th of 100, the first count at which it is no longer the
/// maximum; at the counts these runs actually use — SciFact's 300 judged queries, FiQA's 648,
/// ArguAna's 1,406 — it is 297th of 300, 642nd of 648, 1,392nd of 1,406.
/// <see cref="SampleCount"/> travels with the figures so a reader can tell which regime a row is
/// in.
/// </para>
/// <para>
/// This is deliberately not in <see cref="IrMetrics"/>. That type is retrieval quality, every
/// published quality figure comes out of it, and latency is a different concern that would have to
/// be read past by anyone checking an nDCG.
/// </para>
/// </summary>
/// <param name="SampleCount">
/// How many latencies the figures were read off — one per judged query. On the result rather than
/// implied, because p99 means something different below one hundred samples, and because a count
/// that does not match the run file's query count is the visible symptom of a partial measurement.
/// </param>
/// <param name="P50Milliseconds">
/// The median under the nearest-rank definition above: the sample at index <c>ceil(0.50 n)</c>.
/// </param>
/// <param name="P99Milliseconds">
/// The tail under the same definition: the sample at index <c>ceil(0.99 n)</c>, which is the
/// maximum whenever <see cref="SampleCount"/> is below 100.
/// </param>
public sealed record LatencyStatistics(int SampleCount, double P50Milliseconds, double P99Milliseconds)
{
    /// <summary>
    /// Summarises one entrant's per-query latencies — the values from an
    /// <see cref="EntrantTimings.QueryLatenciesMilliseconds"/> map.
    /// </summary>
    /// <param name="latenciesMilliseconds">
    /// Raw per-query latencies in milliseconds, in any order. Not modified: the caller is usually
    /// still holding the sidecar these came out of.
    /// </param>
    /// <returns>The sample count with p50 and p99, both under the nearest-rank definition.</returns>
    /// <exception cref="ArgumentException">
    /// The sample set is empty — a percentile over nothing has no rank to name, and a
    /// zero-millisecond row would be the best cell in the table produced by the entrant that
    /// measured least — or a sample is negative or non-finite.
    /// </exception>
    public static LatencyStatistics From(IReadOnlyCollection<double> latenciesMilliseconds)
    {
        var sorted = Sorted(latenciesMilliseconds, nameof(latenciesMilliseconds));

        // Sorted once and read twice, so the two figures cannot drift apart the way two separately
        // computed ones could.
        return new LatencyStatistics(
            sorted.Length,
            NearestRank(sorted, 50),
            NearestRank(sorted, 99));
    }

    /// <summary>Reads one percentile off the samples, under the definition in the type remarks.</summary>
    /// <param name="latenciesMilliseconds">
    /// Raw per-query latencies in milliseconds, in any order. Not modified.
    /// </param>
    /// <param name="percentile">
    /// The percentile to read, in <c>(0, 100]</c>. Zero is excluded because nearest rank puts it at
    /// index 0, which names no sample.
    /// </param>
    /// <returns>
    /// The sample at 1-based index <c>ceil(percentile / 100 x n)</c> of the ascending sort — always
    /// an observed latency, never an interpolation between two.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The sample set is empty, or a sample is negative or non-finite.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="percentile"/> is outside <c>(0, 100]</c>, or is not a number.
    /// </exception>
    public static double Percentile(IReadOnlyCollection<double> latenciesMilliseconds, double percentile)
    {
        RequirePercentile(percentile);
        return NearestRank(Sorted(latenciesMilliseconds, nameof(latenciesMilliseconds)), percentile);
    }

    /// <summary>
    /// The samples, validated and copied into ascending order. Copied rather than sorted in place
    /// because the caller's collection is usually a live view over a sidecar it is still reading.
    /// </summary>
    private static double[] Sorted(IReadOnlyCollection<double> samples, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(samples, parameterName);
        if (samples.Count == 0)
        {
            throw new ArgumentException(
                "The latency sample set is empty. A percentile over nothing has no rank to name, " +
                "and the honest failure is here rather than a zero-millisecond cell — which would " +
                "be the fastest row in the table, posted by the entrant that measured least.",
                parameterName);
        }

        var sorted = new double[samples.Count];
        var next = 0;
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample) || sample < 0)
            {
                throw new ArgumentException(
                    $"The latency sample {sample.ToString(CultureInfo.InvariantCulture)} is not a " +
                    "non-negative finite number of milliseconds. A NaN is the dangerous one: it " +
                    "compares false against everything, so a sort can leave it at any index and " +
                    "it would silently become whichever percentile it landed on instead of " +
                    "failing anywhere.",
                    parameterName);
            }

            sorted[next++] = sample;
        }

        Array.Sort(sorted);
        return sorted;
    }

    private static void RequirePercentile(double percentile)
    {
        if (!(percentile > 0 && percentile <= 100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile),
                percentile,
                "The percentile must be in (0, 100]. Nearest rank puts p = 0 at index 0, which " +
                "names no sample, and anything above 100 past the end of the run.");
        }
    }

    /// <summary>
    /// The sample at 1-based index <c>ceil(percentile / 100 x n)</c> — the whole definition, in one
    /// place, so <see cref="From"/> and <see cref="Percentile"/> cannot come to mean two things.
    /// </summary>
    private static double NearestRank(double[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length);

        // Ceiling of a positive product is at least 1 in exact arithmetic; the clamp is against
        // floating-point rounding at the ends, not against a percentile out of range.
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
