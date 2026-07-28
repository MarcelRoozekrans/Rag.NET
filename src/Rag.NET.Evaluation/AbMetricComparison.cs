namespace Rag.NET.Evaluation;

/// <summary>What one metric said about one A/B comparison, and how much of the dataset it saw.</summary>
/// <remarks>
/// <para>
/// Read <see cref="ConfidenceInterval"/> before <see cref="MeanDelta"/>. An A/B run always produces
/// a higher number on one side; an interval spanning zero says the run did not establish which side
/// that is, whatever the mean delta and the tally look like.
/// </para>
/// <para>
/// Then read the drop counts. A flattering delta over four surviving pairs out of fifty is not a
/// result about the dataset, and <see cref="ComparedPairs"/> beside
/// <see cref="DroppedForRunFailure"/> and <see cref="DroppedAsUnscoreable"/> is the only place that
/// is visible.
/// </para>
/// </remarks>
public sealed record AbMetricComparison
{
    /// <summary>Variant A's mean over the pairs that were actually compared; null when there were none.</summary>
    /// <remarks>
    /// Deliberately <b>not</b> the mean the metric reports over each variant's whole run. Those two
    /// means would be taken over different sample sets whenever a pair was dropped, and
    /// <c>MeanB - MeanA</c> would then disagree with <see cref="MeanDelta"/> in the same report.
    /// This mean is over exactly the pairs the delta is over, so the three agree by construction.
    /// </remarks>
    public double? MeanA { get; init; }

    /// <summary>Variant B's mean over the pairs that were actually compared; null when there were none.</summary>
    /// <remarks>Taken over the same pairs as <see cref="MeanA"/>. See the remarks there.</remarks>
    public double? MeanB { get; init; }

    /// <summary>
    /// Mean of the per-sample <c>B - A</c> deltas; null when no pair was comparable.
    /// </summary>
    /// <remarks>
    /// Null rather than <c>0.0</c>. A mean delta of zero says the variants tied, which is a finding.
    /// Null says nothing could be compared, which is not — and once a fabricated zero is in the
    /// report it is indistinguishable from a measured one.
    /// </remarks>
    public double? MeanDelta { get; init; }

    /// <summary>
    /// 95% bootstrap interval on <see cref="MeanDelta"/>; null when no pair was comparable.
    /// </summary>
    /// <remarks>
    /// The only member here that separates a result from noise. A delta of <c>+0.07</c> with
    /// <c>[+0.02, +0.12]</c> is a finding; the same <c>+0.07</c> with <c>[-0.04, +0.18]</c> is not.
    /// </remarks>
    public AbConfidenceInterval? ConfidenceInterval { get; init; }

    /// <summary>How consistently each variant won, over the compared pairs.</summary>
    /// <remarks>
    /// The tally answers "how often" where <see cref="MeanDelta"/> answers "by how much", and they
    /// can disagree: one enormous win carries a mean that eight losses out of ten contradict.
    /// </remarks>
    public AbTally Tally { get; init; } = new(0, 0, 0);

    /// <summary>How many sample pairs this metric's statistics were computed over.</summary>
    public int ComparedPairs { get; init; }

    /// <summary>
    /// Samples dropped because a variant failed to answer them at all.
    /// </summary>
    /// <remarks>
    /// The same for every metric in a report, because a sample one variant never answered cannot be
    /// scored on either side. Kept separate from <see cref="DroppedAsUnscoreable"/> because the two
    /// have different causes and different fixes: this one is a broken pipeline, that one is a
    /// metric that could not read what it was given.
    /// </remarks>
    public int DroppedForRunFailure { get; init; }

    /// <summary>
    /// Pairs dropped because this metric returned no score on one side or the other.
    /// </summary>
    /// <remarks>
    /// Per metric rather than per sample: a sample Faithfulness could not read may still have scored
    /// under Context Recall, so it is dropped from the first and kept by the second. A pair is
    /// all-or-nothing within a metric — keeping the readable half would average the two variants
    /// over different sample sets while still calling the result paired.
    /// </remarks>
    public int DroppedAsUnscoreable { get; init; }
}
