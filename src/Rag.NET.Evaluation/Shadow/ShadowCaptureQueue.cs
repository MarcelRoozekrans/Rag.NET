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

    /// <summary>Enqueues one captured pair; if the queue is full, drops it and counts the drop.</summary>
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
        return _channel.Writer.WriteAsync(capture, cancellationToken);
    }

    /// <summary>Streams captures to the background consumer in arrival order.</summary>
    /// <param name="cancellationToken">Token that stops the stream.</param>
    public IAsyncEnumerable<ShadowCapture> DequeueAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
