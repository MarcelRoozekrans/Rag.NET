namespace Rag.NET.Evaluation;

/// <summary>A single evaluation sample consisting of a question, predicted answer, reference answer, and optionally the retrieved source chunks.</summary>
public sealed record EvaluationSample(
    string Question,
    string PredictedAnswer,
    string ReferenceAnswer,
    IReadOnlyList<string>? SourceChunks = null);
