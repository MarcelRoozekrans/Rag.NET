namespace Rag.NET.Evaluation.Ragas;

/// <summary>Tuning for a RAGAS evaluation run.</summary>
/// <remarks>
/// The concurrency ceiling and the token prices come from <see cref="EvaluationCallOptions"/>,
/// which the dataset builder's options also extend. Property names are unchanged, so setting
/// <c>MaxConcurrentCalls</c> or any of the prices on a <see cref="RagasOptions"/> reads exactly as
/// it did before the move.
/// <para>
/// A RAGAS ceiling is shared across every metric in a suite, not per metric: four registered
/// metrics each fanning out over a 50-chunk sample at a ceiling of 2 would be 8 requests in
/// flight, not 2, and the number a caller sets would then not be the number they get.
/// </para>
/// </remarks>
public sealed class RagasOptions : EvaluationCallOptions
{
    /// <summary>
    /// Number of synthetic questions Answer Relevance generates. Defaults to <c>3</c>.
    /// </summary>
    public int SyntheticQuestionCount { get; set; } = 3;
}
