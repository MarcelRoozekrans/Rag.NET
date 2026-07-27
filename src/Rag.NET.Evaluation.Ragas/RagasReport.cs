namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Aggregated RAGAS scores across a set of evaluation samples.
/// A null value means the metric was not registered in the suite, or no sample could be scored
/// by it. Each mean is taken over the samples the metric could score; unscoreable samples are
/// excluded rather than counted as zero.
/// <see cref="OverallScore"/> is the mean of all registered (non-null) metrics.
/// </summary>
public sealed record RagasReport
{
    public double? Faithfulness     { get; init; }
    public double? AnswerRelevance  { get; init; }
    public double? ContextPrecision { get; init; }
    public double? ContextRecall    { get; init; }
    public double OverallScore      { get; init; }
}
