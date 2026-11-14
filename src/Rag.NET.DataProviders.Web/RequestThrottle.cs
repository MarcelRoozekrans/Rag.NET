namespace Rag.NET.DataProviders.Web;

/// <summary>
/// Spaces the starts of requests at least <c>interval</c> apart, across all callers.
/// </summary>
/// <remarks>
/// A semaphore rather than a per-call delay: ingestion may open several page bodies in parallel,
/// and a delay per caller would let them all fire together after the same wait. Holding the gate
/// only until the slot is claimed — not for the whole request — keeps a slow response from
/// stretching the interval for everyone behind it. Not disposable: a <see cref="SemaphoreSlim"/>
/// whose <c>AvailableWaitHandle</c> is never read holds no unmanaged resource.
/// </remarks>
internal sealed class RequestThrottle
{
    private long _intervalTicks;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastStartTimestamp;
    private bool _hasStarted;

    public RequestThrottle(TimeSpan interval) => _intervalTicks = interval.Ticks;

    /// <summary>
    /// Raises the interval to <paramref name="interval"/> when that is longer; never lowers it.
    /// A site's <c>Crawl-delay</c> comes through here (#927).
    /// </summary>
    /// <remarks>
    /// Lock-free rather than through the gate: a caller holding the gate may be inside a whole
    /// interval's <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, and this is called
    /// synchronously. A wait already in progress keeps the interval it read.
    /// </remarks>
    public void RaiseTo(TimeSpan interval)
    {
        var current = Interlocked.Read(ref _intervalTicks);
        while (interval.Ticks > current)
        {
            var seen = Interlocked.CompareExchange(ref _intervalTicks, interval.Ticks, current);
            if (seen == current) return;
            current = seen;
        }
    }

    /// <summary>Waits until a request may start, then claims that start slot.</summary>
    /// <remarks>
    /// Every start is recorded, even while the interval is zero, so that the first request after a
    /// <see cref="RaiseTo"/> is spaced from the one before it — the robots.txt fetch that carried the
    /// <c>Crawl-delay</c>.
    /// </remarks>
    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hasStarted)
            {
                var interval = TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
                var remaining = interval - TimeProvider.System.GetElapsedTime(_lastStartTimestamp);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                }
            }

            _lastStartTimestamp = TimeProvider.System.GetTimestamp();
            _hasStarted = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
