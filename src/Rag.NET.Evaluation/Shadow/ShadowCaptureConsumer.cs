using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// The background consumer: dequeues captured pairs and persists them to the
/// <see cref="IShadowCaptureStore"/>, off the request path. On shutdown it stops accepting,
/// drains what is already queued within <see cref="ShadowCaptureConsumerOptions.DrainTimeout"/>,
/// and reports how many captures were still unpersisted when the deadline expired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not the ingestion processor's shutdown.</b> <c>IngestionJobProcessor</c>
/// treats cancellation of the stopping token as a clean exit, which abandons everything still
/// queued, silently — the fire-and-forget loss this phase was scoped to close.
/// <see cref="BackgroundService.StopAsync"/> cancels the stopping token the moment shutdown
/// begins, so a drain awaiting that token would cancel immediately and drain nothing. The drain
/// therefore never touches the stopping token: <see cref="StopAsync"/> completes the queue,
/// which turns <see cref="ShadowCaptureQueue.DequeueAllAsync"/> into a finite stream that ends
/// when the buffer is empty, and arms a separate drain-deadline token that fires only when the
/// grace period runs out.
/// </para>
/// <para>
/// <b>Loss is reported, never silent.</b> A capture the queue accepted but that was never
/// persisted — still buffered at the deadline, or dequeued and mid-save when it expired — is
/// counted in <see cref="AbandonedCount"/> and logged once at shutdown. Without that number the
/// real capture rate sits quietly below the configured sample rate, and every offline comparison
/// rests on a denominator nobody can reconstruct.
/// </para>
/// <para>
/// <b>A failed save is not an abandoned save.</b> A store that throws costs exactly one capture:
/// the failure is recorded as a <see cref="ShadowPersistenceFailure"/>, logged with the full
/// exception, and the consumer moves to the next capture — one poisoned capture must not stop
/// every later one.
/// </para>
/// </remarks>
public sealed class ShadowCaptureConsumer : BackgroundService
{
    private readonly ShadowCaptureQueue _queue;
    private readonly IShadowCaptureStore _store;
    private readonly ShadowCaptureConsumerOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _drainDeadline = new();
    private readonly ConcurrentQueue<ShadowPersistenceFailure> _failures = new();
    private long _abandonedInFlight;
    private long _abandoned;

    /// <summary>Creates the consumer over the queue it drains and the store it fills.</summary>
    /// <param name="queue">The queue the request path enqueues captures onto.</param>
    /// <param name="store">Where each dequeued capture is persisted.</param>
    /// <param name="options">The shutdown drain grace period.</param>
    /// <param name="logger">Optional; persistence failures and shutdown loss are logged to it.</param>
    /// <exception cref="ArgumentNullException">Any of the required arguments is null.</exception>
    public ShadowCaptureConsumer(
        ShadowCaptureQueue queue,
        IShadowCaptureStore store,
        ShadowCaptureConsumerOptions options,
        ILogger<ShadowCaptureConsumer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _queue = queue;
        _store = store;
        _options = options;
        _logger = logger ?? NullLogger<ShadowCaptureConsumer>.Instance;
    }

    /// <summary>
    /// How many accepted captures were never persisted because the shutdown drain deadline
    /// expired: those still queued, plus the one mid-save when the deadline hit. Zero until
    /// <see cref="StopAsync"/> has run; exact afterwards.
    /// </summary>
    /// <remarks>
    /// The shutdown counterpart of <see cref="ShadowCaptureQueue.DroppedCount"/>: together they
    /// are the whole gap between the configured sample rate and what the store actually holds,
    /// minus the individually recorded <see cref="FailureSnapshot"/> entries.
    /// </remarks>
    public long AbandonedCount => Interlocked.Read(ref _abandoned);

    /// <summary>A point-in-time copy of every persistence failure so far, in arrival order.</summary>
    /// <remarks>
    /// A copy rather than a live view, for the same reason as
    /// <see cref="InMemoryShadowCaptureStore.Snapshot"/>: readers iterate while the consumer
    /// keeps writing.
    /// </remarks>
    public IReadOnlyList<ShadowPersistenceFailure> FailureSnapshot() => _failures.ToArray();

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately reads with the drain-deadline token, not <paramref name="stoppingToken"/>:
    /// the stop signal must not abandon what is already queued. <see cref="StopAsync"/>
    /// completes the queue, which ends this loop once the buffer is drained; the deadline token
    /// fires only if that drain outlives its grace period.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var capture in _queue.DequeueAllAsync(_drainDeadline.Token).ConfigureAwait(false))
            {
                await PersistAsync(capture).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The drain deadline expired mid-drain. StopAsync counts and reports the remainder.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Stop accepting, drain within the deadline, report the remainder — in that order. The
    /// deadline lives on its own <see cref="CancellationTokenSource"/> because
    /// <see cref="BackgroundService.StopAsync"/> cancels the stopping token before it awaits
    /// <see cref="ExecuteAsync"/>'s task, and a drain bounded by an already-cancelled token
    /// would drain nothing.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // A capture enqueued from here on is refused by the queue and counted as a drop there.
        _queue.Complete();

        // Armed before the base call so an already-in-flight save is bounded too.
        _drainDeadline.CancelAfter(_options.DrainTimeout);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        await DrainRemainderAsync().ConfigureAwait(false);

        ReportAbandonment();
    }

    /// <summary>Persists whatever <see cref="ExecuteAsync"/>'s loop left behind, if anything.</summary>
    /// <remarks>
    /// Usually a no-op: completing the queue ends the streaming loop only after the buffer is
    /// empty. It exists because the runtime schedules <see cref="ExecuteAsync"/> onto the
    /// stopping token rather than invoking it inline, so a stop that arrives early enough
    /// cancels the loop before it ever ran — and then every accepted capture is still queued.
    /// The drain must not depend on the loop having started, only on the queue's contents.
    /// </remarks>
    private async Task DrainRemainderAsync()
    {
        try
        {
            while (_queue.TryDequeue(out var capture))
            {
                await PersistAsync(capture).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The deadline expired mid-drain; what remains is counted and reported below.
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _drainDeadline.Dispose();
        base.Dispose();
    }

    private async Task PersistAsync(ShadowCapture capture)
    {
        try
        {
            // WaitAsync bounds the save even against a store that ignores its token: a drain
            // without a working timeout hangs host shutdown, which is never acceptable.
            await _store.SaveAsync(capture, _drainDeadline.Token)
                .WaitAsync(_drainDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // Dequeued but never persisted: the deadline expired mid-save. Counted with the
            // still-queued remainder rather than as a store failure, then rethrown to end the
            // read loop — the deadline has passed, so there is nothing left to do but report.
            Interlocked.Increment(ref _abandonedInFlight);
            throw;
        }
        catch (Exception exception)
        {
            _failures.Enqueue(ShadowPersistenceFailure.FromException(capture.Question, exception));
            ShadowLog.CapturePersistenceFailed(_logger, exception);
        }
    }

    private void ReportAbandonment()
    {
        var abandoned = Interlocked.Read(ref _abandonedInFlight) + _queue.PendingCount;
        Interlocked.Exchange(ref _abandoned, abandoned);

        if (abandoned > 0)
        {
            ShadowLog.CapturesAbandonedAtShutdown(_logger, abandoned, _options.DrainTimeout);
        }
    }
}
