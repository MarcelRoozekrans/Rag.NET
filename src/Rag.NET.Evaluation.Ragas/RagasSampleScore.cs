namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Every registered metric's score for one evaluation sample, keyed by metric name.
/// </summary>
/// <remarks>
/// Mirrors the <c>LlmJudgeResult.Samples</c> precedent. An aggregate of 0.62 is not actionable on
/// its own; the samples that produced it are. The list is in the order the samples were passed to
/// <see cref="RagasEvaluationSuite.EvaluateAsync"/>, so an index here is an index there.
/// </remarks>
/// <param name="Question">The sample's question, carried so a row can be recognised.</param>
/// <param name="Scores">
/// Score per registered metric name. A <c>null</c> value means the metric could not score this
/// sample — nothing was retrieved, or the model's reply could not be read — which is different
/// from a score of <c>0.0</c>. Such a sample is excluded from that metric's mean and counted in
/// <see cref="RagasReport.UnscoreableSamples"/>.
/// </param>
public sealed record RagasSampleScore(
    string Question,
    IReadOnlyDictionary<string, double?> Scores);
