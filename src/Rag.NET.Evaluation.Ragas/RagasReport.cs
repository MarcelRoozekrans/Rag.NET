namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Aggregated RAGAS scores across a set of evaluation samples.
/// A null metric means it was not registered in the suite, or no sample could be scored by it.
/// Each mean is taken over the samples the metric could score; unscoreable samples are excluded
/// rather than counted as zero, and counted in <see cref="UnscoreableSamples"/>.
/// <see cref="OverallScore"/> is the mean of all registered metrics that produced a score.
/// </summary>
public sealed record RagasReport
{
    /// <summary>Mean Faithfulness, or null when unregistered or nothing was scoreable.</summary>
    public double? Faithfulness     { get; init; }

    /// <summary>Mean Answer Relevance, or null when unregistered or nothing was scoreable.</summary>
    public double? AnswerRelevance  { get; init; }

    /// <summary>Mean Context Precision, or null when unregistered or nothing was scoreable.</summary>
    public double? ContextPrecision { get; init; }

    /// <summary>Mean Context Recall, or null when unregistered or nothing was scoreable.</summary>
    public double? ContextRecall    { get; init; }

    /// <summary>
    /// Mean of the registered metrics that produced a score, or null when none did.
    /// </summary>
    /// <remarks>
    /// Nullable for the same reason the metrics are. A Faithfulness-only run against a model
    /// replying in prose scores <see cref="Faithfulness"/> null, and reporting the overall as
    /// <c>0.0</c> would present "could not be scored" as "maximally bad" one level up from where
    /// that was just fixed — the same fabrication, at the report.
    /// </remarks>
    public double? OverallScore     { get; init; }

    /// <summary>
    /// Per-sample scores, in the order the samples were passed to
    /// <see cref="RagasEvaluationSuite.EvaluateAsync"/>.
    /// </summary>
    /// <remarks>
    /// So a poor aggregate can be traced to the samples that caused it, rather than only observed.
    /// </remarks>
    public IReadOnlyList<RagasSampleScore> Samples { get; init; } = [];

    /// <summary>
    /// How many samples each registered metric could not score, keyed by metric name.
    /// </summary>
    /// <remarks>
    /// A count here is the honest counterpart of the mean above it: the mean says what the scored
    /// samples looked like, and this says how many samples that was silent about. A run where most
    /// samples were unscoreable can still show a flattering mean, and this is the only place that
    /// shows up.
    /// </remarks>
    public IReadOnlyDictionary<string, int> UnscoreableSamples { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}
