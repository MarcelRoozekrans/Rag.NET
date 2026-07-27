using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

internal interface IRagasMetric
{
    /// <summary>True if this metric requires a non-empty ReferenceAnswer on every sample.</summary>
    bool RequiresGroundTruth { get; }

    /// <summary>
    /// Scores one sample (0.0–1.0, higher is better), or returns <c>null</c> when the sample
    /// cannot be scored — nothing was retrieved, or a model reply could not be read.
    /// </summary>
    /// <remarks>
    /// Nullable rather than a sentinel double. Returning 0.0 for "unscoreable" claims the
    /// retrieval was maximally bad, and returning 1.0 claims it was perfect; the pre-3.1 code did
    /// both in different places. A null is excluded from the aggregate, so a degraded run is
    /// visible instead of averaged in.
    /// </remarks>
    /// <param name="sample">The sample to score.</param>
    /// <param name="cancellationToken">Token to cancel the scoring calls.</param>
    Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken);
}
