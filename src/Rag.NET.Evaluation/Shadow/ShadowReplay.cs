using Rag.NET.Evaluation.Internal;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// Stored captures turned into the offline A/B scorer's input: two replaying variants, the sample
/// list, and — separately — the production latency and spend the captures recorded.
/// </summary>
/// <remarks>
/// <para>
/// The bridge Phase 3.8's design promises. Shadow mode captures and does not score; this is the
/// piece that feeds what it captured into <c>RagAbTester.CompareAsync(VariantA, VariantB,
/// Samples)</c>, where each variant's pipeline replays that variant's captured answer and context
/// texts instead of running anything.
/// </para>
/// <para>
/// <b>Ignore the report's own latency and cost for a replayed comparison — read
/// <see cref="Latency"/> and <see cref="Spend"/> here instead.</b> The scorer times the live call
/// it makes, which for a replay is a dictionary lookup: it will dutifully report 0.2&#160;ms for a
/// pipeline that really took 900&#160;ms in production. The real figures are the ones the captures
/// carry, so they are computed here, from the captures, in the same shape the report uses. The
/// report's quality metrics and failure accounting are trustworthy as-is; only its two live
/// measurements are meaningless under replay, because a replay makes no live calls to measure.
/// </para>
/// <para>
/// <b>Reference answers can be supplied later, and that is the point of capturing.</b> Production
/// traffic has no ground truth, so an unannotated replay scores with the reference-free metrics
/// only — Context Precision and Context Recall refuse an empty reference. Annotate the captured
/// questions afterwards, pass the answers to <see cref="From"/>, and all four RAGAS metrics run
/// over traffic that was production's.
/// </para>
/// </remarks>
public sealed record ShadowReplay
{
    /// <summary>The primary, replaying its captured answers. The <c>A</c> side of every delta.</summary>
    public required AbVariant VariantA { get; init; }

    /// <summary>The shadowed secondary, replaying its captured answers. Deltas are <c>B - A</c>.</summary>
    public required AbVariant VariantB { get; init; }

    /// <summary>
    /// One sample per capture, in capture order: the captured question, plus whatever reference
    /// answer was supplied for it — empty when none was, because production carries none.
    /// </summary>
    /// <remarks>
    /// Pass this list to the scorer <b>unfiltered and unreordered</b>: the replaying pipelines
    /// serve the captures by position, and verify the question on every call, so a subset or a
    /// shuffle fails loudly rather than pairing answers with the wrong questions. To compare a
    /// subset, build a replay from that subset of captures.
    /// </remarks>
    public required IReadOnlyList<EvaluationSample> Samples { get; init; }

    /// <summary>
    /// The production latency comparison, computed from the captured wall-clocks — the figures to
    /// read <i>instead of</i> the report's latency section, which under replay times an in-memory
    /// lookup.
    /// </summary>
    /// <remarks>
    /// Paired over the captures where <b>both</b> sides carry a latency; a failed side carries
    /// none, so its pair drops out here exactly as it drops out of the scoring. Same shape as the
    /// report's section so the substitution is mechanical, and every figure in it is a wall-clock
    /// that really happened in production.
    /// </remarks>
    public required AbLatencyComparison Latency { get; init; }

    /// <summary>
    /// Total captured spend by variant name — the figures to read <i>instead of</i> the report's
    /// cost section, which under replay has no ledgers to read and reports nothing.
    /// </summary>
    /// <remarks>
    /// A side is present only when <b>every</b> capture measured its spend; summing the captures
    /// that happened to be measured would present an undercount as the total. The primary is
    /// therefore absent by design: it serves concurrent traffic on a shared ledger, so no honest
    /// per-request figure exists and none was captured — the same "absent, never zero" rule as
    /// <see cref="AbReport.Cost"/>. A failed side's spend counts; the call cost money before it
    /// threw.
    /// </remarks>
    public required IReadOnlyDictionary<string, decimal> Spend { get; init; }

    /// <summary>Builds the scorer's input from stored captures.</summary>
    /// <param name="captures">
    /// The captured pairs to replay, all from the same primary/secondary variant pair. Read them
    /// from wherever the application's <see cref="IShadowCaptureStore"/> persisted them.
    /// </param>
    /// <param name="referenceAnswers">
    /// Ground truth added after capture, keyed by question (ordinal). Questions without an entry
    /// keep an empty reference. Omit it entirely and only the reference-free metrics can score.
    /// </param>
    /// <param name="options">
    /// Tuning for the captured-latency interval, matching what the comparison itself will use;
    /// defaults are used when omitted.
    /// </param>
    /// <returns>The replay: hand its variants and samples to <c>RagAbTester.CompareAsync</c>.</returns>
    /// <exception cref="ArgumentException">
    /// The capture set is empty — there is nothing to replay, and not even variant names to build
    /// a comparison from — or it mixes captures from different variant pairs.
    /// </exception>
    public static ShadowReplay From(
        IReadOnlyList<ShadowCapture> captures,
        IReadOnlyDictionary<string, string>? referenceAnswers = null,
        AbOptions? options = null)
    {
        Validate(captures);

        return new ShadowReplay
        {
            VariantA = new AbVariant(
                captures[0].Primary.VariantName,
                new ShadowReplayPipeline(captures, static capture => capture.Primary)),
            VariantB = new AbVariant(
                captures[0].Secondary.VariantName,
                new ShadowReplayPipeline(captures, static capture => capture.Secondary)),
            Samples = BuildSamples(captures, referenceAnswers),
            Latency = CapturedLatency(captures, options ?? new AbOptions()),
            Spend = CapturedSpend(captures),
        };
    }

    /// <summary>Refuses a set the scorer could only mis-report, while the caller can still see why.</summary>
    private static void Validate(IReadOnlyList<ShadowCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(captures);

        if (captures.Count == 0)
        {
            throw new ArgumentException(
                "There are no captures to replay. An empty set carries no answers to score and " +
                "not even the variant names a comparison is keyed by, so it is refused here rather " +
                "than failing somewhere inside the scorer.", nameof(captures));
        }

        var primary = captures[0].Primary.VariantName;
        var secondary = captures[0].Secondary.VariantName;

        for (var i = 1; i < captures.Count; i++)
        {
            if (!string.Equals(captures[i].Primary.VariantName, primary, StringComparison.Ordinal)
                || !string.Equals(captures[i].Secondary.VariantName, secondary, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The capture at index {i} is a '{captures[i].Primary.VariantName}'/" +
                    $"'{captures[i].Secondary.VariantName}' pair in a replay of '{primary}'/'{secondary}'. " +
                    "One replay compares one pair of configurations; filter the stored captures by " +
                    "variant pair before building it.", nameof(captures));
            }
        }
    }

    /// <summary>One sample per capture, in capture order.</summary>
    /// <remarks>
    /// The predicted answer is left empty: the scorer takes each variant's answer from the
    /// variant's own pipeline — here, the replay — and reads only the question and the reference
    /// from the sample.
    /// </remarks>
    private static EvaluationSample[] BuildSamples(
        IReadOnlyList<ShadowCapture> captures,
        IReadOnlyDictionary<string, string>? referenceAnswers)
    {
        var samples = new EvaluationSample[captures.Count];
        for (var i = 0; i < captures.Count; i++)
        {
            var question = captures[i].Question;
            var reference = referenceAnswers is not null
                && referenceAnswers.TryGetValue(question, out var supplied)
                ? supplied
                : "";

            samples[i] = new EvaluationSample(question, PredictedAnswer: "", ReferenceAnswer: reference);
        }

        return samples;
    }

    /// <summary>The latency comparison production actually measured.</summary>
    private static AbLatencyComparison CapturedLatency(IReadOnlyList<ShadowCapture> captures, AbOptions options)
    {
        var latenciesA = new TimeSpan[captures.Count];
        var latenciesB = new TimeSpan[captures.Count];
        var deltas = new double[captures.Count];
        var pairs = 0;

        for (var i = 0; i < captures.Count; i++)
        {
            if (captures[i].Primary.Latency is { } primary && captures[i].Secondary.Latency is { } secondary)
            {
                latenciesA[pairs] = primary;
                latenciesB[pairs] = secondary;
                deltas[pairs] = (secondary - primary).TotalMilliseconds;
                pairs++;
            }
        }

        // Percentiles need sorted values; the deltas must stay in capture order until the
        // bootstrap resamples them — the same arrangement as the live comparison's.
        Array.Sort(latenciesA, 0, pairs);
        Array.Sort(latenciesB, 0, pairs);

        var random = options.Seed is { } seed ? new Random(seed) : new Random();

        return new AbLatencyComparison
        {
            MedianA = AbStatistics.Percentile(latenciesA.AsSpan(0, pairs), 0.50),
            Percentile95A = AbStatistics.Percentile(latenciesA.AsSpan(0, pairs), 0.95),
            MedianB = AbStatistics.Percentile(latenciesB.AsSpan(0, pairs), 0.50),
            Percentile95B = AbStatistics.Percentile(latenciesB.AsSpan(0, pairs), 0.95),
            MeanDeltaMilliseconds = AbStatistics.MeanDelta(deltas.AsSpan(0, pairs)),
            ConfidenceIntervalMilliseconds =
                AbStatistics.BootstrapMeanDeltaCi(deltas.AsSpan(0, pairs), options.BootstrapResamples, random),
            ComparedPairs = pairs,
        };
    }

    /// <summary>Each fully measured side's captured spend; an unmeasured side is absent, never zero.</summary>
    private static Dictionary<string, decimal> CapturedSpend(IReadOnlyList<ShadowCapture> captures)
    {
        var spend = new Dictionary<string, decimal>(2, StringComparer.Ordinal);

        Record(spend, captures, static capture => capture.Primary);
        Record(spend, captures, static capture => capture.Secondary);

        return spend;

        static void Record(
            Dictionary<string, decimal> spend,
            IReadOnlyList<ShadowCapture> captures,
            Func<ShadowCapture, ShadowVariantCapture> side)
        {
            var total = 0m;
            for (var i = 0; i < captures.Count; i++)
            {
                // One unmeasured capture and the side has no figure at all: a sum over whichever
                // captures happened to be measured would present an undercount as the total.
                if (side(captures[i]).Spend is not { } measured)
                    return;

                total += measured;
            }

            spend[side(captures[0]).VariantName] = total;
        }
    }
}
