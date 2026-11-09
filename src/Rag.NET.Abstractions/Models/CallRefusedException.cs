namespace Rag.NET.Models;

/// <summary>
/// Thrown <em>instead of</em> calling a model, on purpose — by this library or by a decorator
/// around a client. A cost budget that is spent, or a benchmark cache opened refuse-on-miss, raises
/// one of these so that nothing downstream mistakes the refusal for a failed provider call.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is never a provider failure.</b> No model was called, so there is nothing to retry, no
/// fallback provider that could answer instead, and no <see cref="ModelCallException"/> to report.
/// Every call-site wrap in Rag.NET rethrows this untouched before translating anything else, and
/// the resilience decorators treat it as non-transient by type, so a refusal can never cause the
/// spending or the cache-blending it exists to prevent.
/// </para>
/// <para>
/// <b>It exists because a call site cannot tell a refusal from a failure by any other means
/// (#504, #930).</b> The client or embedder a component holds is frequently a stack of decorators,
/// and a component that wraps every exception from it as a model failure relabels a deliberate
/// stop as "the model failed". Deriving from this type is how a refusal says what it is.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/>, so code that already catches that keeps
/// working. <see cref="BudgetExceededException"/> is the library's own refusal.
/// </para>
/// </remarks>
public class CallRefusedException : InvalidOperationException
{
    /// <summary>Creates a refusal with a generic message. Prefer one that says what was refused.</summary>
    public CallRefusedException()
        : base("The call was refused deliberately; no model was called.")
    {
    }

    /// <summary>Creates a refusal with a message saying what was refused and why.</summary>
    /// <param name="message">What was refused, and why.</param>
    public CallRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal that carries the exception that prompted it.</summary>
    /// <param name="message">What was refused, and why.</param>
    /// <param name="innerException">The exception that prompted the refusal, if any.</param>
    public CallRefusedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
