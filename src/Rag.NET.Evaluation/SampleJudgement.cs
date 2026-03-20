namespace Rag.NET.Evaluation;

/// <summary>All criteria scores for a single evaluated sample.</summary>
public sealed record SampleJudgement(
    string Question,
    IReadOnlyDictionary<string, CriterionScore> Criteria);
