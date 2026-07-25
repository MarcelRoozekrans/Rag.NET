namespace Rag.NET.Abstractions;

/// <summary>
/// Throttles outbound calls by making callers wait (asynchronously) until a permit is
/// available, rather than rejecting them.
/// </summary>
public interface IRateLimiter : IDisposable
{
    /// <summary>
    /// Waits until <paramref name="permits"/> permits are available, then consumes them.
    /// Throws only on cancellation or when a bounded wait queue is full — never merely
    /// because the caller is over the rate.
    /// </summary>
    /// <param name="permits">Number of permits to acquire. Defaults to 1 (one request).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    /// <exception cref="InvalidOperationException">
    /// The wait queue is bounded and full, so the call was rejected instead of queued.
    /// </exception>
    ValueTask AcquireAsync(int permits = 1, CancellationToken cancellationToken = default);
}
