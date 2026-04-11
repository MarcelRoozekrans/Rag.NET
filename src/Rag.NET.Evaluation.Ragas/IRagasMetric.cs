using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

internal interface IRagasMetric
{
    /// <summary>True if this metric requires a non-empty ReferenceAnswer on every sample.</summary>
    bool RequiresGroundTruth { get; }

    /// <summary>Score a single sample (0.0–1.0, higher is better).</summary>
    Task<double> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken);
}
