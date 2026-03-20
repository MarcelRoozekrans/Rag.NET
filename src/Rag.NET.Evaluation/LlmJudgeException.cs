namespace Rag.NET.Evaluation;

/// <summary>
/// Thrown when the LLM judge returns a response that cannot be parsed
/// or is missing required criteria.
/// </summary>
public sealed class LlmJudgeException(string message, string rawResponse)
    : Exception(message)
{
    /// <summary>The raw response text returned by the LLM, included for diagnosis.</summary>
    public string RawResponse { get; } = rawResponse;
}
