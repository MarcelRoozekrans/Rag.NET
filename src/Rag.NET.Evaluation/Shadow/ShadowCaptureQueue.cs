using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// The bounded queue between the request path and the background capture consumer. It carries
/// <see cref="PendingShadowCapture"/> work — the secondary has not run yet — and a full queue
/// drops the incoming item and counts the loss; it never blocks the enqueuer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="BoundedChannelFullMode.Wait"/></b>, which the ingestion queue
/// uses. Waiting applies backpressure, and backpressure here would couple the primary request's
/// latency to the secondary's throughput — a slow or stuck secondary would throttle the very
/// requests it is supposed to be invisible to, which is the failure shadow mode exists to prevent.
/// </para>
/// <para>
/// <b><see cref="BoundedChannelFullMode.DropWrite"/> loses the incoming item, never a queued
/// one.</b> An accepted item that later vanished (as <c>DropOldest</c> allows) would be a
/// second, harder-to-reason-about kind of loss; dropping the write is equivalent to the sample
/// never having been taken — literally so, since a dropped item's secondary never runs and costs
/// nothing. Every drop is counted via the channel's drop callback — which runs synchronously
/// inside the write — so <see cref="DroppedCount"/> is exact, not a best-effort estimate. A
/// silent drop would put the real capture rate quietly below the configured sample rate, leaving
/// every offline comparison resting on a denominator nobody can reconstruct.
/// </para>
/// </remarks>
public sealed class ShadowCaptureQueue
{
    private readonly Channel<PendingShadowCapture> _channel;
    private long _dropped;

    /// <summary>Creates the queue with capacity <see cref="ShadowCaptureQueueOptions.Capacity"/>.</summary>
    /// <param name="options">Capacity of the queue; validated where it is set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ShadowCaptureQueue(ShadowCaptureQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _channel = Channel.CreateBounded<PendingShadowCapture>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
            },
            _ =>
            {
                Interlocked.Increment(ref _dropped);
                ShadowTelemetry.Dropped.Add(1);
            });
    }

    /// <summary>How many samples were dropped because the queue was full. Exact, monotonic.</summary>
    /// <remarks>
    /// Each dropped item was a sampled request that will never become a stored pair — its
    /// secondary never ran — so this number is the gap between the configured sample rate and
    /// the real capture rate.
    /// </remarks>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>How many accepted items are queued and not yet dequeued.</summary>
    /// <remarks>
    /// What shutdown reads to report the remainder: after <see cref="Complete"/>, whatever the
    /// consumer's drain deadline left here is loss, and loss is reported, never silent.
    /// </remarks>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Enqueues one pending capture; if the queue is full or completed, drops it and counts the drop.</summary>
    /// <param name="pending">The work to hand to the background consumer.</param>
    /// <param name="cancellationToken">Token observed before the write.</param>
    /// <returns>A task that is already complete unless the token was cancelled.</returns>
    /// <remarks>
    /// Under <see cref="BoundedChannelFullMode.DropWrite"/> a write always completes
    /// synchronously — accepted or dropped — so this call cannot block the request path.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pending"/> is null.</exception>
    public ValueTask EnqueueAsync(PendingShadowCapture pending, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        // Counts the offer, dropped or not: on the meter, enqueued tracks the configured sample
        // rate and enqueued − dropped is what the queue accepted — see ShadowTelemetry for the
        // full identity down to what the store holds.
        ShadowTelemetry.Enqueued.Add(1);

        if (!_channel.Writer.TryWrite(pending))
        {
            // TryWrite only refuses on a completed channel — a full one accepts the write and
            // drops it through the counting callback. An item refused after Complete is as
            // lost as one dropped on overflow, and an uncounted loss would put the real capture
            // rate quietly below the configured sample rate, so it is counted the same way.
            Interlocked.Increment(ref _dropped);
            ShadowTelemetry.Dropped.Add(1);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Streams pending captures to the background consumer in arrival order.</summary>
    /// <param name="cancellationToken">Token that stops the stream.</param>
    public IAsyncEnumerable<PendingShadowCapture> DequeueAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Dequeues one pending capture if any is queued, without waiting.</summary>
    /// <param name="pending">The dequeued item, or null when the queue was empty.</param>
    /// <returns>Whether an item was dequeued.</returns>
    /// <remarks>
    /// The shutdown drain reads with this rather than <see cref="DequeueAllAsync"/>: the drain
    /// must also work when the consumer's streaming loop never ran at all, and a non-waiting
    /// read is what lets it stop the moment the buffer is empty.
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out PendingShadowCapture pending)
        => _channel.Reader.TryRead(out pending);

    /// <summary>Stops accepting: later enqueues become counted drops, and the dequeue stream ends
    /// once the buffered items are read.</summary>
    /// <remarks>
    /// The first half of the shutdown contract. Completing the writer is what lets the consumer
    /// drain without a token: <see cref="DequeueAllAsync"/> becomes a finite stream that ends
    /// exactly when the buffer is empty, instead of an infinite one that must be cancelled — and
    /// cancelling it is how queued work gets abandoned.
    /// </remarks>
    public void Complete() => _channel.Writer.TryComplete();
}
