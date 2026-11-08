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
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastStartTimestamp;
    private bool _hasStarted;

    public RequestThrottle(TimeSpan interval) => _interval = interval;

    /// <summary>Waits until a request may start, then claims that start slot.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        if (_interval <= TimeSpan.Zero) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hasStarted)
            {
                var remaining = _interval - TimeProvider.System.GetElapsedTime(_lastStartTimestamp);
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
