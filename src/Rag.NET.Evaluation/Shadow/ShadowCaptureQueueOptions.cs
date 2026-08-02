namespace Rag.NET.Evaluation.Shadow;

/// <summary>Tuning for the bounded queue between the request path and the capture consumer.</summary>
public sealed class ShadowCaptureQueueOptions
{
    /// <summary>How many captures the queue holds before it starts dropping. Default 1000.</summary>
    /// <remarks>
    /// The bound is what keeps a slow consumer from turning into unbounded memory growth; the
    /// drop-on-full policy (see <see cref="ShadowCaptureQueue"/>) is what keeps the bound from
    /// turning into backpressure on the request path. Raising the capacity buys tolerance for
    /// consumer stalls at the price of memory holding verbatim production text; it never changes
    /// what happens when the queue is genuinely full — drop and count.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not positive. A zero-capacity queue is not a way to switch shadow mode off —
    /// the sample rate is — it would only mean every capture is taken, costed, and then dropped.
    /// Validated here rather than where the channel is built, so the exception names the property
    /// that was set.
    /// </exception>
    public int Capacity
    {
        get => _capacity;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"{nameof(Capacity)} must be positive. A zero-capacity queue would not switch " +
                    "shadow mode off — the sample rate does that — it would only mean every " +
                    "capture is taken, costed, and then dropped.");
            }

            _capacity = value;
        }
    }

    private readonly int _capacity = 1000;
}
