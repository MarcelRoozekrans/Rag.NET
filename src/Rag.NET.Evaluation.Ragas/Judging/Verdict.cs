namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>The outcome of a single yes/no judgement.</summary>
/// <remarks>
/// Tri-state rather than <c>bool</c> because a verdict the model did not give is not a verdict.
/// Collapsing "unparseable" into "no" biases every score downward silently; collapsing it into
/// "yes" biases upward. Both fabricate. This makes the third case the caller's problem, which is
/// the only place it can be handled honestly.
/// </remarks>
internal enum Verdict
{
    /// <summary>The model affirmed.</summary>
    Yes,

    /// <summary>The model denied.</summary>
    No,

    /// <summary>The model's reply could not be read as either.</summary>
    Unparseable,
}
