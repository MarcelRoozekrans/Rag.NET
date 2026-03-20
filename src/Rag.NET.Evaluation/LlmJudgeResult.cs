namespace Rag.NET.Evaluation;

/// <summary>
/// Result of an LLM judge evaluation run.
/// Contains per-criterion scores and reasoning for each sample.
/// </summary>
public sealed record LlmJudgeResult(IReadOnlyList<SampleJudgement> Samples)
{
    /// <summary>
    /// Returns the mean score across all samples for the given criterion.
    /// Returns 0.0 if no sample contains that criterion.
    /// </summary>
    public double MeanScore(string criterion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterion);
        var scores = Samples
            .Where(s => s.Criteria.ContainsKey(criterion))
            .Select(s => s.Criteria[criterion].Score)
            .ToList();
        return scores.Count == 0 ? 0.0 : scores.Average();
    }

    /// <summary>
    /// Returns true if every sample that contains the criterion meets or exceeds the threshold.
    /// Returns true vacuously if no sample contains that criterion.
    /// </summary>
    public bool AllPass(string criterion, double threshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterion);
        return Samples
            .Where(s => s.Criteria.ContainsKey(criterion))
            .All(s => s.Criteria[criterion].Score >= threshold);
    }
}
