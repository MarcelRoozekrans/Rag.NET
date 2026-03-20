namespace Rag.NET.Evaluation;

/// <summary>
/// Thrown when the LLM judge returns a response that cannot be parsed
/// or is missing required criteria.
/// </summary>
public sealed class LlmJudgeException : Exception
{
    /// <summary>The raw response text returned by the LLM, included for diagnosis.</summary>
    public string RawResponse { get; }

    /// <summary>Initializes a new instance with a message and the raw LLM response.</summary>
    public LlmJudgeException(string message, string rawResponse)
        : base(message)
    {
        RawResponse = rawResponse;
    }

    /// <inheritdoc/>
    public LlmJudgeException() : base()
    {
        RawResponse = string.Empty;
    }

    /// <inheritdoc/>
    public LlmJudgeException(string message) : base(message)
    {
        RawResponse = string.Empty;
    }

    /// <inheritdoc/>
    public LlmJudgeException(string message, Exception innerException)
        : base(message, innerException)
    {
        RawResponse = string.Empty;
    }
}
