using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// The bounded queue between the request path and the background capture consumer. A full queue
/// drops the incoming capture and counts the loss; it never blocks the enqueuer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="BoundedChannelFullMode.Wait"/></b>, which the ingestion queue
/// uses. Waiting applies backpressure, and backpressure here would couple the primary request's
/// latency to the secondary's throughput — a slow or stuck secondary would throttle the very
/// requests it is supposed to be invisible to, which is the failure shadow mode exists to prevent.
/// </para>
/// <para>
/// <b><see cref="BoundedChannelFullMode.DropWrite"/> loses the incoming capture, never a queued
/// one.</b> An accepted capture that later vanished (as <c>DropOldest</c> allows) would be a
/// second, harder-to-reason-about kind of loss; dropping the write is equivalent to the sample
/// never having been taken. Every drop is counted via the channel's drop callback — which runs
/// synchronously inside the write — so <see cref="DroppedCount"/> is exact, not a best-effort
/// estimate. A silent drop would put the real capture rate quietly below the configured sample
/// rate, leaving every offline comparison resting on a denominator nobody can reconstruct.
/// </para>
/// </remarks>
public sealed class ShadowCaptureQueue
{
    private readonly Channel<ShadowCapture> _channel;
    private long _dropped;

    /// <summary>Creates the queue with capacity <see cref="ShadowCaptureQueueOptions.Capacity"/>.</summary>
    /// <param name="options">Capacity of the queue; validated where it is set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ShadowCaptureQueue(ShadowCaptureQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _channel = Channel.CreateBounded<ShadowCapture>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>How many captures were dropped because the queue was full. Exact, monotonic.</summary>
    /// <remarks>
    /// Each dropped capture was a sampled request whose secondary already ran and cost money, so
    /// this number is the gap between the configured sample rate and the real capture rate.
    /// </remarks>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>How many accepted captures are queued and not yet dequeued.</summary>
    /// <remarks>
    /// What shutdown reads to report the remainder: after <see cref="Complete"/>, whatever the
    /// consumer's drain deadline left here is loss, and loss is reported, never silent.
    /// </remarks>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Enqueues one captured pair; if the queue is full or completed, drops it and counts the drop.</summary>
    /// <param name="capture">The pair to hand to the background consumer.</param>
    /// <param name="cancellationToken">Token observed before the write.</param>
    /// <returns>A task that is already complete unless the token was cancelled.</returns>
    /// <remarks>
    /// Under <see cref="BoundedChannelFullMode.DropWrite"/> a write always completes
    /// synchronously — accepted or dropped — so this call cannot block the request path.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is null.</exception>
    public ValueTask EnqueueAsync(ShadowCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        if (!_channel.Writer.TryWrite(capture))
        {
            // TryWrite only refuses on a completed channel — a full one accepts the write and
            // drops it through the counting callback. A capture refused after Complete is as
            // lost as one dropped on overflow, and an uncounted loss would put the real capture
            // rate quietly below the configured sample rate, so it is counted the same way.
            Interlocked.Increment(ref _dropped);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Streams captures to the background consumer in arrival order.</summary>
    /// <param name="cancellationToken">Token that stops the stream.</param>
    public IAsyncEnumerable<ShadowCapture> DequeueAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Dequeues one capture if any is queued, without waiting.</summary>
    /// <param name="capture">The dequeued capture, or null when the queue was empty.</param>
    /// <returns>Whether a capture was dequeued.</returns>
    /// <remarks>
    /// The shutdown drain reads with this rather than <see cref="DequeueAllAsync"/>: the drain
    /// must also work when the consumer's streaming loop never ran at all, and a non-waiting
    /// read is what lets it stop the moment the buffer is empty.
    /// </remarks>
    public bool TryDequeue([MaybeNullWhen(false)] out ShadowCapture capture)
        => _channel.Reader.TryRead(out capture);

    /// <summary>Stops accepting: later enqueues become counted drops, and the dequeue stream ends
    /// once the buffered captures are read.</summary>
    /// <remarks>
    /// The first half of the shutdown contract. Completing the writer is what lets the consumer
    /// drain without a token: <see cref="DequeueAllAsync"/> becomes a finite stream that ends
    /// exactly when the buffer is empty, instead of an infinite one that must be cancelled — and
    /// cancelling it is how queued captures get abandoned.
    /// </remarks>
    public void Complete() => _channel.Writer.TryComplete();
}
