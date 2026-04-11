namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Aggregated RAGAS scores across a set of evaluation samples.
/// Null values indicate a metric was not registered in the suite.
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
