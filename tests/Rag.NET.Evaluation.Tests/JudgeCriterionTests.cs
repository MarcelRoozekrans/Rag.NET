using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class JudgeCriterionTests
{
    [Fact]
    public void Correctness_HasExpectedName()
        => Assert.Equal("correctness", JudgeCriterion.Correctness.Name);

    [Fact]
    public void Faithfulness_HasExpectedName()
        => Assert.Equal("faithfulness", JudgeCriterion.Faithfulness.Name);

    [Fact]
    public void Relevance_HasExpectedName()
        => Assert.Equal("relevance", JudgeCriterion.Relevance.Name);

    [Fact]
    public void AllDefaults_HaveNonEmptyDescriptions()
    {
        Assert.NotEmpty(JudgeCriterion.Correctness.Description);
        Assert.NotEmpty(JudgeCriterion.Faithfulness.Description);
        Assert.NotEmpty(JudgeCriterion.Relevance.Description);
    }
}
