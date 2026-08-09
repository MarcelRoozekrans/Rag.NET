using System.Globalization;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The reproducibility gate over the Phase 5.1 cost measurement: <b>no cost figure may be
/// published from a single run.</b> Given two or more repeat runs of one entrant on one dataset —
/// sidecars kept apart by <see cref="TimingsSidecar.PathFor(string, int)"/>'s run-index
/// convention — it compares what they measured and produces the only publishable thing: the
/// spread.
/// <para>
/// <b>Why it exists — the defect that got through.</b> The embedding cache's disk reads once sat
/// inside the timed spans, and identical SciFact/LangChain runs — same code, same corpus, same
/// cache hits — measured 55.2 s with a cold OS page cache and 2.4 s with a hot one: 23x on run
/// order alone. Every single-file validation passed, because each sidecar was internally
/// impeccable; only two runs side by side could show the number was about the machine's state
/// rather than the library. This type is that comparison, made mandatory before publication.
/// </para>
/// <para>
/// <b>The visible spread is the protection; the hard bar is only a backstop.</b> <see cref="From"/>
/// always returns min, max and ratio for every figure, pass or not, and the published figure
/// carries that range instead of hiding behind one number. A tolerance that quietly passes at
/// 1.9x and fails at 2.1x is exactly the kind of threshold that gets gamed or ignored, so the
/// bar is deliberately not the interesting part: <see cref="HardFailRatio"/> exists to make the
/// unarguable cases — the 23x class — fail unambiguously, and everything milder is judged by a
/// reader looking at the spread the result always shows.
/// </para>
/// <para>
/// <b>p99 is reported, never gated.</b> At the sample counts these runs use, p99 is read off one
/// to three tail samples — below one hundred queries it is simply the worst observation, and at
/// SciFact's 300 it is the 297th — and two healthy LlamaIndex runs on the same corpus measured
/// 1032.7 ms against 393.7 ms, a 2.6x spread from tail noise alone, already at the level a
/// defect-catching bar would need. Gating p99 would therefore either fail honest runs or need a
/// bar too loose to catch anything the gated figures miss — and it would miss nothing, because
/// the defect class this gate exists for is state leaking into a timed span, which inflates
/// every sample and so moves the gated p50 and indexing figures too. The p99 spread still
/// travels with the result, so an unstable tail is visible in exactly the place it would
/// otherwise be quietly published.
/// </para>
/// </summary>
/// <param name="RunTag">The one entrant all repeats ran — every repeat must carry this tag.</param>
/// <param name="RunCount">How many repeat runs the spread was read off, at least two.</param>
/// <param name="IndexingSeconds">
/// The spread of <see cref="EntrantTimings.IndexingSeconds"/> across the repeats. Gated.
/// </param>
/// <param name="P50Milliseconds">
/// The spread of nearest-rank p50 — computed per repeat by <see cref="LatencyStatistics"/>, the
/// one definition every entrant gets. Gated.
/// </param>
/// <param name="P99Milliseconds">
/// The spread of nearest-rank p99 across the repeats. Reported, never gated — the type remarks
/// say why.
/// </param>
public sealed record CostReproducibility(
    string RunTag,
    int RunCount,
    MeasurementSpread IndexingSeconds,
    MeasurementSpread P50Milliseconds,
    MeasurementSpread P99Milliseconds)
{
    /// <summary>
    /// The backstop: a run-to-run ratio above this on a gated figure — indexing wall-clock or p50
    /// — fails <see cref="From"/> outright.
    /// <para>
    /// <b>Why 3.</b> The bar must sit above everything honest repeats do and below everything the
    /// defect class does, with room on both sides. Above: the largest legitimate spread observed
    /// on a gated figure was 1.4x — SciFact/LangChain indexing at 2.9 s and 2.1 s back-to-back —
    /// and 2.2x (2.9 s against 1.3 s) only when the machine's state was deliberately disturbed
    /// between runs by a full no-incremental Release rebuild, which is already outside the "one
    /// machine, one session" protocol every published row must follow. Below: the defect class
    /// this gate exists to catch — external state such as the OS page cache inside a timed span —
    /// measured 23x. Three is more than double the worst in-protocol jitter yet nearly an order
    /// of magnitude under the observed defect, so neither an honest run nor a broken one lands
    /// near it; and because the spread is always returned, a 2.5x run passes loudly, not quietly.
    /// </para>
    /// </summary>
    public const double HardFailRatio = 3;

    /// <summary>
    /// Compares repeat runs of one entrant on one dataset and returns the spread of every cost
    /// figure — the gate every published figure must come through.
    /// </summary>
    /// <param name="repeats">
    /// The repeat runs' timings, two or more, in any order. All must carry the same run tag and
    /// time the same judged-query set: the tag is the entrant's identity and the query-id set is
    /// the dataset's fingerprint in the sidecar, so a mismatch on either means these are not
    /// repeats of one measurement and no spread exists between them.
    /// </param>
    /// <returns>The spread of indexing seconds, p50 and p99 across the repeats — never a single
    /// run's numbers.</returns>
    /// <exception cref="ArgumentException">
    /// Fewer than two runs (a single run cannot show whether its number is about the library or
    /// about the machine's state, so no figure exists at n = 1), runs tagged for different
    /// entrants, runs timing different query sets, a non-finite or negative indexing time, or a
    /// latency map <see cref="LatencyStatistics"/> refuses.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A gated figure's spread is past <see cref="HardFailRatio"/>, naming both extreme values —
    /// the 23x case fails here, unambiguously.
    /// </exception>
    public static CostReproducibility From(IReadOnlyList<EntrantTimings> repeats)
    {
        ArgumentNullException.ThrowIfNull(repeats);
        RequireRepeats(repeats);
        RequireOneEntrant(repeats);
        RequireOneQuerySet(repeats);

        var indexing = new double[repeats.Count];
        var p50 = new double[repeats.Count];
        var p99 = new double[repeats.Count];
        for (var run = 0; run < repeats.Count; run++)
        {
            RequireMeasurableIndexing(repeats[run].IndexingSeconds, run + 1, nameof(repeats));
            indexing[run] = repeats[run].IndexingSeconds;
            var statistics = LatencyStatistics.From([.. repeats[run].QueryLatenciesMilliseconds.Values]);
            p50[run] = statistics.P50Milliseconds;
            p99[run] = statistics.P99Milliseconds;
        }

        var result = new CostReproducibility(
            repeats[0].RunTag, repeats.Count, Spread(indexing), Spread(p50), Spread(p99));
        result.RequireWithinBar(result.IndexingSeconds, "indexing wall-clock", "s");
        result.RequireWithinBar(result.P50Milliseconds, "query-latency p50", "ms");
        return result;
    }

    /// <summary>
    /// Reads <paramref name="runCount"/> repeat sidecars beside one run file — indices 1 through
    /// <paramref name="runCount"/> under <see cref="TimingsSidecar.PathFor(string, int)"/> — and
    /// gates them with <see cref="From"/>. Every repeat goes through the full
    /// <see cref="TimingsSidecar.ReadPaired(string, int)"/> check first, so a repeat that belongs
    /// to another entrant or dataset fails as a pairing error before any spread is computed.
    /// </summary>
    /// <param name="runFilePath">The run file whose repeat sidecars to read.</param>
    /// <param name="runCount">How many repeats were run, at least two.</param>
    /// <returns>The spread across all repeats, as <see cref="From"/> returns it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="runCount"/> is below two — no cost figure may be published from a single
    /// run, so asking for one is refused before any file is touched.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The run file or any repeat's sidecar is absent — refuse-on-miss, naming the indexed path,
    /// exactly as <see cref="TimingsSidecar.ReadPaired(string, int)"/> refuses.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A sidecar is malformed or mispaired, or a gated figure's spread is past
    /// <see cref="HardFailRatio"/>.
    /// </exception>
    public static CostReproducibility FromSidecars(string runFilePath, int runCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFilePath);
        if (runCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runCount),
                runCount,
                "No cost figure may be published from a single run: the 23x embedding-cache " +
                "defect measured 55.2 s and 2.4 s for identical work, and one run cannot show " +
                "which kind of number it produced. Run the entrant at least twice.");
        }

        var repeats = new EntrantTimings[runCount];
        for (var run = 0; run < runCount; run++)
        {
            repeats[run] = TimingsSidecar.ReadPaired(runFilePath, run + 1);
        }

        return From(repeats);
    }

    private static void RequireRepeats(IReadOnlyList<EntrantTimings> repeats)
    {
        if (repeats.Count >= 2)
        {
            return;
        }

        throw new ArgumentException(
            repeats.Count == 0
                ? "The repeat list is empty: nothing ran, so there is nothing to compare, let " +
                  "alone publish."
                : "No cost figure may be published from a single run: the 23x embedding-cache " +
                  "defect measured 55.2 s and 2.4 s for identical work, and a single run cannot " +
                  "show which kind of number it produced. Run the entrant at least twice — " +
                  "TimingsSidecar's run index keeps the repeats apart — and compare them here.",
            nameof(repeats));
    }

    private static void RequireOneEntrant(IReadOnlyList<EntrantTimings> repeats)
    {
        var tag = repeats[0].RunTag;
        for (var run = 1; run < repeats.Count; run++)
        {
            if (!string.Equals(tag, repeats[run].RunTag, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Run 1 is tagged '{tag}' but run " +
                    $"{(run + 1).ToString(CultureInfo.InvariantCulture)} is tagged " +
                    $"'{repeats[run].RunTag}'. Repeats of one measurement carry one tag; two " +
                    "tags are two entrants, and comparing them here would launder a difference " +
                    "between libraries into a 'reproducible' spread — or fail one library for " +
                    "not being the other.",
                    nameof(repeats));
            }
        }
    }

    private static void RequireOneQuerySet(IReadOnlyList<EntrantTimings> repeats)
    {
        var first = repeats[0].QueryLatenciesMilliseconds;
        for (var run = 1; run < repeats.Count; run++)
        {
            var current = repeats[run].QueryLatenciesMilliseconds;
            var differing = FirstOnlyInFormer(current, first) ?? FirstOnlyInFormer(first, current);
            if (differing is not null)
            {
                throw new ArgumentException(
                    $"Run {(run + 1).ToString(CultureInfo.InvariantCulture)} and run 1 disagree " +
                    $"about the judged-query set — query '{differing}' is timed by only one of " +
                    "them. Same entrant, different queries: these are runs over different " +
                    "datasets or splits, not repeats of one measurement, and a spread between " +
                    "them would compare two different jobs.",
                    nameof(repeats));
            }
        }
    }

    /// <summary>The ordinal-first query id in the former map only, or null when none is — the
    /// ordinal choice keeps the error message stable across dictionary orderings.</summary>
    private static string? FirstOnlyInFormer(
        IReadOnlyDictionary<string, double> former, IReadOnlyDictionary<string, double> latter)
    {
        string? differing = null;
        foreach (var queryId in former.Keys)
        {
            if (!latter.ContainsKey(queryId)
                && (differing is null || string.CompareOrdinal(queryId, differing) < 0))
            {
                differing = queryId;
            }
        }

        return differing;
    }

    private static void RequireMeasurableIndexing(double indexingSeconds, int run, string parameterName)
    {
        if (!double.IsFinite(indexingSeconds) || indexingSeconds < 0)
        {
            throw new ArgumentException(
                $"Run {run.ToString(CultureInfo.InvariantCulture)}'s indexing wall-clock is " +
                $"{indexingSeconds.ToString(CultureInfo.InvariantCulture)}, not a non-negative " +
                "finite number of seconds. A NaN would make the spread's ratio NaN, which " +
                "compares false against the bar — the gate would pass by not comparing.",
                parameterName);
        }
    }

    /// <summary>The extremes of a non-empty sample of measured values, as a spread.</summary>
    private static MeasurementSpread Spread(double[] values)
    {
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var value in values)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        return new MeasurementSpread(minimum, maximum);
    }

    private void RequireWithinBar(MeasurementSpread spread, string figure, string unit)
    {
        if (spread.Ratio <= HardFailRatio)
        {
            return;
        }

        throw new InvalidDataException(
            $"The {figure} is not reproducible across " +
            $"{RunCount.ToString(CultureInfo.InvariantCulture)} repeat runs of '{RunTag}': " +
            $"{spread.Minimum.ToString(CultureInfo.InvariantCulture)} {unit} in one repeat and " +
            $"{spread.Maximum.ToString(CultureInfo.InvariantCulture)} {unit} in another — " +
            $"{spread.Ratio.ToString("0.0", CultureInfo.InvariantCulture)}x, past the " +
            $"{HardFailRatio.ToString(CultureInfo.InvariantCulture)}x hard-fail bar. Honest " +
            "repeats have never measured close to this; a spread this size is the signature of " +
            "state outside the library sitting inside a timed span — the defect class that " +
            "measured 55.2 s against 2.4 s (23x) on OS page-cache order alone. Neither number " +
            "is the figure; find what leaked into the span.");
    }
}
