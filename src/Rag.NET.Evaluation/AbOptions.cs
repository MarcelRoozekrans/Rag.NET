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

    /// <summary>How many bootstrap resamples to draw per interval. Default 2000.</summary>
    /// <remarks>
    /// More resamples reduce the Monte-Carlo jitter of the interval itself; they do not narrow it,
    /// because the width is set by the data. Raising this does not buy a more confident answer.
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
