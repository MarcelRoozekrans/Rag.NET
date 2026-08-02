namespace Rag.NET.Evaluation.Shadow;

/// <summary>Tuning for the background consumer that persists captured pairs.</summary>
public sealed class ShadowCaptureConsumerOptions
{
    /// <summary>How long shutdown may keep draining queued captures. Default 5 seconds.</summary>
    /// <remarks>
    /// The drain must be bounded — an unbounded drain against a stuck store hangs host
    /// shutdown — and the bound's cost must be visible: whatever is still unpersisted when the
    /// deadline expires is counted in <see cref="ShadowCaptureConsumer.AbandonedCount"/> and
    /// logged. Zero is a valid setting and means no grace at all: everything still queued at
    /// stop is abandoned, counted, and reported.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative. Validated here rather than where the deadline timer is armed, so
    /// the exception surfaces where the option is set instead of during shutdown.
    /// </exception>
    public TimeSpan DrainTimeout
    {
        get => _drainTimeout;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"{nameof(DrainTimeout)} must be zero or positive. Zero means no drain " +
                    "grace at all — everything still queued at stop is abandoned and " +
                    "reported — and a negative timeout means nothing.");
            }

            _drainTimeout = value;
        }
    }

    private readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(5);
}
