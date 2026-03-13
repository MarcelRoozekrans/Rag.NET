namespace Rag.NET.Evaluation;

/// <summary>
/// Result of an evaluation run.
/// <see cref="MeanScore"/> is the average cosine similarity across all samples (0–1, higher is better).
/// </summary>
public sealed record EvaluationResult(
    double MeanScore,
    IReadOnlyList<double> Scores);
