namespace Rag.NET.Evaluation;

/// <summary>A single question/answer pair to evaluate.</summary>
public sealed record EvaluationSample(
    string Question,
    string PredictedAnswer,
    string ReferenceAnswer,
    IReadOnlyList<string>? SourceChunks = null);
