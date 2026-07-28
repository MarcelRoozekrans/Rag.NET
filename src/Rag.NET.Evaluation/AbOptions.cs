namespace Rag.NET.Evaluation;

/// <summary>Tuning for one A/B comparison.</summary>
/// <remarks>
/// Nothing here changes what is measured — only how the interval around the measurement is drawn
/// and where the tie band sits. The defaults are the ones the design settled on and are fine to
/// leave alone.
/// </remarks>
public sealed class AbOptions
{
    /// <summary>
    /// Seeds the bootstrap. Null — the default — draws a fresh resample on every comparison, so the
    /// interval moves slightly from run to run even when the deltas are identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarantee is exactly this: <b>the same seed over the same deltas gives the same
    /// interval.</b> An unreproducible confidence interval is not evidence, which is the same rule
    /// <see cref="EvaluationDatasetBuilderOptions.Seed"/> establishes for sampling.
    /// </para>
    /// <para>
    /// It does <b>not</b> make the pipelines deterministic. Both variants are asked real questions
    /// by a real model, and above temperature 0 the same question yields a different answer on every
    /// run. Two comparisons over the same dataset with the same seed can therefore produce different
    /// deltas, and so different intervals.
    /// </para>
    /// <para>
    /// It does <b>not</b> make the judge deterministic either. RAGAS scores come from an LLM, and a
    /// sample that scored 0.8 once can score 0.7 next time or become unscoreable altogether. The
    /// seed fixes the resampling of whatever deltas the run produced, not the production of them.
    /// </para>
    /// <para>
    /// What it is genuinely for: rerunning the statistics over a stored set of deltas, and pinning
    /// the interval in a test.
    /// </para>
    /// </remarks>
    public int? Seed { get; init; }

    /// <summary>How many bootstrap resamples to draw per interval. Default 2000, minimum 1000.</summary>
    /// <remarks>
    /// <para>
    /// More resamples reduce the Monte-Carlo jitter of the interval itself; they do not narrow it,
    /// because the width is set by the data. Raising this does not buy a more confident answer.
    /// </para>
    /// <para>
    /// <b>Lowering it is not the way to make a slow run faster, and below 1000 it throws.</b> The
    /// resampling is <c>pairs * resamples</c> multiply-adds over an array already in memory — a
    /// 50-sample comparison at the default is 100k operations, which is nothing beside the two LLM
    /// runs that produced the scores. Wall-clock is spent in the pipelines and the judge, not here.
    /// </para>
    /// <para>
    /// The floor exists because a 95% interval trims <c>floor(0.025 * resamples)</c> values from each
    /// tail, and that expression is <b>zero for every value at or below 39</b>. With nothing trimmed,
    /// the interval becomes the smallest and largest resample mean — whose expected coverage is
    /// <c>(B - 1) / (B + 1)</c>, so 0.818 at 10 resamples — while still being reported as 95%. It
    /// under-covers, which means it excludes zero, which means it names winners that are not there.
    /// That is the one failure this whole comparison exists to prevent, so it is refused rather than
    /// silently allowed.
    /// </para>
    /// </remarks>
    public int BootstrapResamples { get; init; } = 2000;

    /// <summary>
    /// Half-width of the tie band used by the win/loss/tie tally. Default <c>1e-9</c>.
    /// </summary>
    /// <remarks>
    /// Per-sample deltas within <c>[-TieEpsilon, +TieEpsilon]</c> count as ties. The default is
    /// floating-point noise only — two identically scored samples must not land in a win column
    /// because of the last bit. Widen it deliberately if a difference below some size is not
    /// interesting to you; that is a judgement about the metric, not about arithmetic.
    /// </remarks>
    public double TieEpsilon { get; init; } = 1e-9;
}
