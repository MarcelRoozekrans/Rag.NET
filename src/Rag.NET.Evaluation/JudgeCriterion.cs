namespace Rag.NET.Evaluation;

/// <summary>A named evaluation criterion with a rubric description for the LLM judge.</summary>
public sealed record JudgeCriterion(string Name, string Description)
{
    /// <summary>Checks whether the predicted answer is factually correct given the reference answer.</summary>
    public static readonly JudgeCriterion Correctness = new(
        "correctness",
        "Is the predicted answer factually correct given the reference answer?");

    /// <summary>Checks whether the predicted answer stays grounded in the retrieved context without hallucinating.</summary>
    public static readonly JudgeCriterion Faithfulness = new(
        "faithfulness",
        "Does the predicted answer stay grounded in the retrieved context without hallucinating facts not present in the context?");

    /// <summary>Checks whether the predicted answer directly and completely addresses the question.</summary>
    public static readonly JudgeCriterion Relevance = new(
        "relevance",
        "Does the predicted answer directly and completely address the question?");
}
