using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class LlmJudgeResultTests
{
    private static LlmJudgeResult MakeResult(params (string criterion, double score)[] perSample)
    {
        var judgements = new List<SampleJudgement>();
        foreach (var (criterion, score) in perSample)
        {
            judgements.Add(new SampleJudgement(
                Question: "q",
                Criteria: new Dictionary<string, CriterionScore>(StringComparer.Ordinal)
                {
                    [criterion] = new CriterionScore(score, "reason"),
                }));
        }
        return new LlmJudgeResult(judgements);
    }

    [Fact]
    public void MeanScore_AveragesAcrossSamples()
    {
        var result = MakeResult(("correctness", 0.8), ("correctness", 0.6));
        Assert.Equal(0.7, result.MeanScore("correctness"), precision: 10);
    }

    [Fact]
    public void MeanScore_WhenCriterionAbsent_ReturnsZero()
    {
        var result = MakeResult(("correctness", 0.8));
        Assert.Equal(0.0, result.MeanScore("relevance"), precision: 10);
    }

    [Fact]
    public void AllPass_WhenAllMeetThreshold_ReturnsTrue()
    {
        var result = MakeResult(("correctness", 0.8), ("correctness", 0.9));
        Assert.True(result.AllPass("correctness", 0.7));
    }

    [Fact]
    public void AllPass_WhenOneFails_ReturnsFalse()
    {
        var result = MakeResult(("correctness", 0.8), ("correctness", 0.5));
        Assert.False(result.AllPass("correctness", 0.7));
    }

    [Fact]
    public void AllPass_WhenCriterionAbsent_ReturnsTrue()
    {
        var result = MakeResult(("correctness", 0.8));
        Assert.True(result.AllPass("relevance", 0.7));
    }

    [Fact]
    public void AllPass_WhenScoreEqualsThreshold_ReturnsTrue()
    {
        var result = MakeResult(("correctness", 0.7));
        Assert.True(result.AllPass("correctness", 0.7));
    }

    [Fact]
    public void MeanScore_EmptySamples_ReturnsZero()
    {
        var result = new LlmJudgeResult([]);
        Assert.Equal(0.0, result.MeanScore("correctness"), precision: 10);
    }
}
