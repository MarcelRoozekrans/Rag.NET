using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// The background consumer: dequeues pending shadow work, <b>runs the secondary pipeline</b> —
/// here, never on the request path — and persists the finished pair to the
/// <see cref="IShadowCaptureStore"/>. On shutdown it stops accepting, drains what is already
/// queued within <see cref="ShadowCaptureConsumerOptions.DrainTimeout"/>, and reports how many
/// captures were still unpersisted when the deadline expired.
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
/// <b>Loss is reported, never silent.</b> An item the queue accepted but that was never
/// persisted — still buffered at the deadline, or dequeued and mid-secondary-run or mid-save
/// when it expired — is counted in <see cref="AbandonedCount"/> and logged once at shutdown. Without that number the
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
    private readonly IShadowCaptureSanitiser? _sanitiser;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _drainDeadline = new();
    private readonly ConcurrentQueue<ShadowPersistenceFailure> _failures = new();
    private long _abandonedInFlight;
    private long _abandoned;

    /// <summary>Creates the consumer over the queue it drains and the store it fills.</summary>
    /// <param name="queue">The queue the request path enqueues captures onto.</param>
    /// <param name="store">Where each dequeued capture is persisted.</param>
    /// <param name="options">The shutdown drain grace period and the optional secondary cost ledger.</param>
    /// <param name="sanitiser">
    /// Optional scrubber applied to every capture before the store sees it. <b>Without one,
    /// captures persist verbatim</b> — the user's question and the retrieved document text,
    /// exactly as they were; see <see cref="IShadowCaptureSanitiser"/> for why that default is
    /// deliberate and how the seam fails safe.
    /// </param>
    /// <param name="logger">Optional; persistence failures and shutdown loss are logged to it.</param>
    /// <exception cref="ArgumentNullException">Any of the required arguments is null.</exception>
    public ShadowCaptureConsumer(
        ShadowCaptureQueue queue,
        IShadowCaptureStore store,
        ShadowCaptureConsumerOptions options,
        IShadowCaptureSanitiser? sanitiser = null,
        ILogger<ShadowCaptureConsumer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _queue = queue;
        _store = store;
        _options = options;
        _sanitiser = sanitiser;
        _logger = logger ?? NullLogger<ShadowCaptureConsumer>.Instance;
    }

    /// <summary>
    /// How many accepted captures were never persisted because the shutdown drain deadline
    /// expired: those still queued, plus the one mid-secondary-run or mid-save when the deadline
    /// hit. Zero until <see cref="StopAsync"/> has run; exact afterwards.
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
            await foreach (var pending in _queue.DequeueAllAsync(_drainDeadline.Token).ConfigureAwait(false))
            {
                await ProcessAsync(pending).ConfigureAwait(false);
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
            while (_queue.TryDequeue(out var pending))
            {
                await ProcessAsync(pending).ConfigureAwait(false);
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

    /// <summary>Runs the secondary the pending item still owes, then persists the finished pair.</summary>
    private async Task ProcessAsync(PendingShadowCapture pending)
    {
        var capture = await RunSecondaryAsync(pending).ConfigureAwait(false);
        await PersistAsync(capture).ConfigureAwait(false);
    }

    /// <summary>
    /// The secondary run itself — here, on the consumer, and never on the request path. A
    /// secondary that throws becomes a <see cref="ShadowVariantFailure"/> inside a persisted
    /// pair: a result for the offline comparison, because dropping failed secondaries would bias
    /// it toward whatever the secondary handles well. When a
    /// <see cref="ShadowCaptureConsumerOptions.SecondaryCostLedger"/> is configured, the run's
    /// spend is measured around it — before/after day-window readings, honest because this
    /// consumer runs secondaries one at a time — and recorded even for a run that threw, which
    /// can have cost money before it failed.
    /// </summary>
    private async Task<ShadowCapture> RunSecondaryAsync(PendingShadowCapture pending)
    {
        // Safe outside the try: the spend read never throws. If the drain deadline has already
        // expired, it returns absent and the run below is what observes the deadline.
        var spendBefore = await ReadSecondarySpendAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            // WaitAsync bounds the run even against a pipeline that ignores its token: a drain
            // without a working timeout hangs host shutdown, which is never acceptable.
            var response = await pending.SecondaryPipeline
                .AskAsync(pending.Question, pending.Options, _drainDeadline.Token)
                .WaitAsync(_drainDeadline.Token).ConfigureAwait(false);

            var latency = Stopwatch.GetElapsedTime(started);
            var spend = SpendDelta(spendBefore, await ReadSecondarySpendAsync().ConfigureAwait(false));

            if (response is null)
            {
                return pending.ToCapture(Failed(
                    pending.SecondaryVariantName,
                    new ShadowVariantFailure(pending.SecondaryVariantName, "The pipeline returned no response."))
                    with
                { Spend = spend });
            }

            return pending.ToCapture(new ShadowVariantCapture(
                pending.SecondaryVariantName,
                response.Answer,
                ShadowContextTexts.From(response.Sources),
                latency,
                spend));
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // Dequeued but never completed: the drain deadline expired mid-run. Counted with the
            // still-queued remainder — the pair does not exist yet, so there is nothing to record
            // as a failure — then rethrown to end the drain.
            Interlocked.Increment(ref _abandonedInFlight);
            throw;
        }
        catch (Exception exception)
        {
            ShadowLog.SecondaryRunFailed(_logger, exception);
            var spend = SpendDelta(spendBefore, await ReadSecondarySpendAsync().ConfigureAwait(false));
            return pending.ToCapture(Failed(
                pending.SecondaryVariantName,
                ShadowVariantFailure.FromException(pending.SecondaryVariantName, exception))
                with
            { Spend = spend });
        }

        // The AbVariant rollover rule, applied per capture: a run that crosses UTC midnight
        // empties the day bucket between the readings, so a shrinking reading is dropped rather
        // than recorded — and absent beats zero, because a zero would claim the run was free.
        // A local function because the analyzers disagree about passing Nullable<decimal> to a
        // private method (EPS05 wants `in`, RCS1242 forbids it); locals are exempt, the same
        // shape RagAbTester's spend recording uses.
        static decimal? SpendDelta(decimal? before, decimal? after) =>
            before is { } first && after is { } second && second >= first ? second - first : null;
    }

    /// <summary>
    /// One day-window reading of the dedicated secondary ledger, or absent — when no ledger is
    /// configured, and when the configured one cannot be read.
    /// </summary>
    /// <remarks>
    /// Never throws, including on cancellation: a spend reading is a best-effort figure and is
    /// never worth failing or abandoning the capture over. The run and the save observe the
    /// drain deadline themselves, so shutdown still cannot hang; the <c>WaitAsync</c> here
    /// bounds the read against a ledger that ignores its token, mirroring both.
    /// </remarks>
    private async Task<decimal?> ReadSecondarySpendAsync()
    {
        if (_options.SecondaryCostLedger is not { } ledger)
        {
            return null;
        }

        try
        {
            return await ledger.GetSpendAsync(CostWindow.Day, _drainDeadline.Token)
                .WaitAsync(_drainDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ShadowLog.SecondarySpendReadFailed(_logger, exception);
            return null;
        }
    }

    private static ShadowVariantCapture Failed(string variantName, ShadowVariantFailure failure) =>
        new(variantName, Answer: null, ContextTexts: [], Failure: failure);

    private async Task PersistAsync(ShadowCapture capture)
    {
        try
        {
            // Sanitised inside the try, so a sanitiser that throws costs the capture — counted
            // and recorded below — rather than letting it through unsanitised, which is the only
            // safe direction for that seam to fail in.
            var sanitised = await SanitiseAsync(capture).ConfigureAwait(false);

            // WaitAsync bounds the save even against a store that ignores its token: a drain
            // without a working timeout hangs host shutdown, which is never acceptable.
            await _store.SaveAsync(sanitised, _drainDeadline.Token)
                .WaitAsync(_drainDeadline.Token).ConfigureAwait(false);
            ShadowTelemetry.Processed.Add(1);
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
            ShadowTelemetry.Failed.Add(1);
            ShadowLog.CapturePersistenceFailed(_logger, exception);
        }
    }

    /// <summary>The capture as it may be persisted: scrubbed when a sanitiser is configured, verbatim otherwise.</summary>
    /// <remarks>
    /// WaitAsync bounds the sanitiser like the run and the save — an LLM-backed scrubber that
    /// ignores its token must not hang shutdown. A null result is refused here, inside the
    /// caller's try, so it is recorded as a lost capture rather than handed to the store.
    /// </remarks>
    private async Task<ShadowCapture> SanitiseAsync(ShadowCapture capture)
    {
        if (_sanitiser is null)
        {
            return capture;
        }

        var sanitised = await _sanitiser.SanitiseAsync(capture, _drainDeadline.Token)
            .WaitAsync(_drainDeadline.Token).ConfigureAwait(false);

        return sanitised ?? throw new InvalidOperationException(
            "The shadow capture sanitiser returned null. The capture is treated as lost rather " +
            "than persisted unsanitised.");
    }

    private void ReportAbandonment()
    {
        var abandoned = Interlocked.Read(ref _abandonedInFlight) + _queue.PendingCount;
        Interlocked.Exchange(ref _abandoned, abandoned);

        if (abandoned > 0)
        {
            ShadowTelemetry.Abandoned.Add(abandoned);
            ShadowLog.CapturesAbandonedAtShutdown(_logger, abandoned, _options.DrainTimeout);
        }
    }
}
