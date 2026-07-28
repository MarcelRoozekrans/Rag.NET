namespace Rag.NET.Evaluation;

/// <summary>Wall-clock per variant, and the paired difference between them.</summary>
/// <remarks>
/// <para>
/// Latency is measured by the runner rather than read off the response, because
/// <c>RagResponse</c> carries only the answer and its sources. It is the measurement most sensitive
/// to execution order — whichever variant runs second gets a warm cache — which is why the runner
/// alternates the lead.
/// </para>
/// <para>
/// The percentiles are over the comparable samples only, so both variants' figures come from the
/// same set of questions. Nearest-rank convention: the p-th percentile of <c>n</c> sorted values is
/// the value at rank <c>ceil(p x n)</c>, one-based, so p50 of an even-length set is the <b>lower</b>
/// of the two middle values rather than their average — <c>ceil(0.5 x 4)</c> is rank 2 of 4. No
/// interpolation, so every reported figure is a wall-clock that really happened.
/// </para>
/// </remarks>
public sealed record AbLatencyComparison
{
    /// <summary>Variant A's median latency over the comparable samples; null when there were none.</summary>
    public TimeSpan? MedianA { get; init; }

    /// <summary>Variant A's 95th-percentile latency over the comparable samples; null when there were none.</summary>
    public TimeSpan? Percentile95A { get; init; }

    /// <summary>Variant B's median latency over the comparable samples; null when there were none.</summary>
    public TimeSpan? MedianB { get; init; }

    /// <summary>Variant B's 95th-percentile latency over the comparable samples; null when there were none.</summary>
    public TimeSpan? Percentile95B { get; init; }

    /// <summary>
    /// Mean per-sample <c>B - A</c> latency in milliseconds; null when no sample was comparable.
    /// Positive means B was slower.
    /// </summary>
    /// <remarks>
    /// Milliseconds rather than a <see cref="TimeSpan"/> because it shares
    /// <see cref="ConfidenceIntervalMilliseconds"/>'s units, and a signed duration reported next to
    /// an unsigned interval invites the two to be read in different scales. Null rather than zero,
    /// for the same reason as everywhere else in this report.
    /// </remarks>
    public double? MeanDeltaMilliseconds { get; init; }

    /// <summary>
    /// 95% bootstrap interval on <see cref="MeanDeltaMilliseconds"/>, in milliseconds; null when no
    /// sample was comparable.
    /// </summary>
    /// <remarks>
    /// Spanning zero means the run did not establish which variant is faster. Latency measured over
    /// a live provider is noisy enough that this happens often, and a mean delta on its own would
    /// hide it.
    /// </remarks>
    public AbConfidenceInterval? ConfidenceIntervalMilliseconds { get; init; }

    /// <summary>How many sample pairs the latency figures were computed over.</summary>
    public int ComparedPairs { get; init; }
}
