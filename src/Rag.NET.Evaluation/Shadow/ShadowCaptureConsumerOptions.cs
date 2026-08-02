using Rag.NET.Abstractions;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>Tuning for the background consumer that persists captured pairs.</summary>
public sealed class ShadowCaptureConsumerOptions
{
    /// <summary>
    /// Optional ledger <b>dedicated to the secondary pipeline</b>. Set it — and wire the same
    /// ledger into the secondary's own cost tracking — and every capture records what its
    /// secondary run cost, so the doubled spend shadow mode adds is visible per request rather
    /// than as an unexplained rise on a bill. Default <see langword="null"/>: spend is recorded
    /// as absent, never as a zero that would claim the run was free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured as <c>AbVariant.CostLedger</c> measures a variant: the day window is read before
    /// and after the run and the difference attributed to it. That diff is honest here only
    /// because the consumer runs secondaries <b>one at a time</b> — so the ledger must be this
    /// pipeline's alone. Any other traffic writing to it lands in some capture's figure.
    /// </para>
    /// <para>
    /// The same two refusals as <c>AbVariant</c>, applied per capture: a run that crosses UTC
    /// midnight empties the day bucket between the readings, making the difference meaningless,
    /// so it is dropped and that capture's spend is absent; and a ledger that cannot be read
    /// costs the figure, never the capture. <b>The primary's spend is deliberately not measured
    /// this way</b>: primaries serve concurrent production traffic, so a per-request diff over
    /// their shared ledger would include every overlapping request — a fabricated number, worse
    /// than an honest absence. The primary side's <see cref="ShadowVariantCapture.Spend"/> stays
    /// <see langword="null"/>.
    /// </para>
    /// </remarks>
    public ICostLedger? SecondaryCostLedger { get; init; }

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
