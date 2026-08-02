namespace Rag.NET.Evaluation.Shadow;

/// <summary>A variant that did not answer a shadowed request, and what it said instead.</summary>
/// <remarks>
/// <para>
/// A secondary that threw is a result: dropping failed secondaries would bias every offline
/// comparison toward whatever the secondary happens to handle well, so the failure is captured
/// alongside the answered pairs.
/// </para>
/// <para>
/// Strings only, unlike the scorer's internal failure type, which carries the
/// <see cref="Exception"/> itself for its in-process report. A capture exists to be persisted —
/// file, table, blob, queue — and an exception object does not survive a store; what does survive
/// is the same <c>"{TypeName}: {Message}"</c> line the offline report prints, which
/// <see cref="FromException"/> produces.
/// </para>
/// </remarks>
/// <param name="VariantName">The variant that failed.</param>
/// <param name="Message">
/// A short account for the offline report: the exception type and message, or a description of an
/// unusable response.
/// </param>
public sealed record ShadowVariantFailure(string VariantName, string Message)
{
    /// <summary>Builds the failure line the offline report expects from a caught exception.</summary>
    /// <param name="variantName">The variant that threw.</param>
    /// <param name="exception">The exception it threw.</param>
    /// <returns>A failure whose message reads <c>"{TypeName}: {Message}"</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public static ShadowVariantFailure FromException(string variantName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Type as well as message, matching the line the offline scorer writes for a failure it
        // caught itself: "Object reference not set to an instance of an object" alone tells a
        // reader nothing about which layer gave way.
        return new ShadowVariantFailure(variantName, $"{exception.GetType().Name}: {exception.Message}");
    }
}
