namespace Rag.NET.Evaluation;

/// <summary>Score and reasoning for a single criterion on a single evaluated sample.</summary>
public sealed record CriterionScore(double Score, string Reasoning);
