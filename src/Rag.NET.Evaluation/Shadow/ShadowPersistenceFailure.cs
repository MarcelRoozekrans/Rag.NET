namespace Rag.NET.Evaluation.Shadow;

/// <summary>A capture the consumer dequeued but could not persist, and why.</summary>
/// <remarks>
/// <para>
/// The persistence sibling of <see cref="ShadowVariantFailure"/>: that type records a pipeline
/// that failed to answer, this one records a store that failed to keep the pair. It exists so a
/// throwing store costs exactly one capture and one record — the consumer notes the failure and
/// moves to the next capture, rather than letting one poisoned save kill every later one.
/// </para>
/// <para>
/// The question, not the whole capture, is kept: enough to identify which pair was lost without
/// pinning both variants' full answer and context text in memory for the process lifetime. The
/// count of these records is the gap between what the queue delivered and what the store holds.
/// </para>
/// </remarks>
/// <param name="Question">The captured question, identifying which pair was lost.</param>
/// <param name="Message">
/// A short account of the failure: the exception type and message, as
/// <see cref="FromException"/> builds it.
/// </param>
public sealed record ShadowPersistenceFailure(string Question, string Message)
{
    /// <summary>Builds the failure line from the exception the store threw.</summary>
    /// <param name="question">The captured question, identifying which pair was lost.</param>
    /// <param name="exception">The exception the store threw.</param>
    /// <returns>A failure whose message reads <c>"{TypeName}: {Message}"</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static ShadowPersistenceFailure FromException(string question, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Type as well as message, matching ShadowVariantFailure.FromException: the message
        // alone tells a reader nothing about which layer gave way.
        return new ShadowPersistenceFailure(
            question, $"{exception.GetType().Name}: {exception.Message}");
    }
}
