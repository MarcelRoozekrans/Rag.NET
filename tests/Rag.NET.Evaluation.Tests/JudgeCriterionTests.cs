using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class JudgeCriterionTests
{
    [Fact]
    public void Correctness_HasExpectedNameAndDescription()
    {
        Assert.Equal("correctness", JudgeCriterion.Correctness.Name);
        Assert.Equal(
            "Is the predicted answer factually correct given the reference answer?",
            JudgeCriterion.Correctness.Description);
    }

    [Fact]
    public void Faithfulness_HasExpectedNameAndDescription()
    {
        Assert.Equal("faithfulness", JudgeCriterion.Faithfulness.Name);
        Assert.Equal(
            "Does the predicted answer stay grounded in the retrieved context without hallucinating facts not present in the context?",
            JudgeCriterion.Faithfulness.Description);
    }

    [Fact]
    public void Relevance_HasExpectedNameAndDescription()
    {
        Assert.Equal("relevance", JudgeCriterion.Relevance.Name);
        Assert.Equal(
            "Does the predicted answer directly and completely address the question?",
            JudgeCriterion.Relevance.Description);
    }

    [Fact]
    public void RecordEquality_SameNameAndDescription_AreEqual()
    {
        var a = new JudgeCriterion("my-criterion", "Does the thing.");
        var b = new JudgeCriterion("my-criterion", "Does the thing.");
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentName_AreNotEqual()
    {
        var a = new JudgeCriterion("criterion-a", "Does the thing.");
        var b = new JudgeCriterion("criterion-b", "Does the thing.");
        Assert.NotEqual(a, b);
    }
}
