namespace Rag.NET.Evaluation.Internal;

/// <summary>A variant that did not answer one sample, and what it said instead.</summary>
/// <remarks>
/// <c>IRagPipeline.AskAsync</c> throws rather than returning a result, so a failure has to be
/// caught to keep one bad sample from losing the other ninety-nine. The message is kept for the
/// report; the pair it belongs to is excluded from every statistic either way.
/// </remarks>
/// <param name="VariantName">The variant that failed.</param>
/// <param name="Message">
/// A short account for the report: the exception type and message, or a description of an unusable
/// response.
/// </param>
internal sealed record VariantFailure(string VariantName, string Message)
{
    /// <summary>
    /// The exception itself, when there was one. <see langword="null"/> when the variant returned
    /// without throwing but gave nothing usable.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="Message"/> rather than folded into it: a report wants one line,
    /// and whoever has to fix the variant wants the stack trace.
    /// </remarks>
    public Exception? Exception { get; init; }
}
