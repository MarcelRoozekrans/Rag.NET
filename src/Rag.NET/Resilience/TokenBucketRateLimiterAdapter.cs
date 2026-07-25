using System.Diagnostics;
using System.Threading.RateLimiting;
using Rag.NET.Abstractions;
using Rag.NET.Telemetry;

namespace Rag.NET.Resilience;

/// <summary>
/// <see cref="IRateLimiter"/> over <see cref="TokenBucketRateLimiter"/>, configured from a
/// requests-per-minute budget. Callers over the rate wait for a token; they are only
/// rejected when a bounded wait queue overflows.
/// </summary>
/// <remarks>
/// Bucket derivation (pinned by <c>RateLimiterTests</c>): the per-minute budget is spread
/// over 1-second replenishment periods — <c>TokensPerPeriod = max(1, requestsPerMinute / 60)</c>
/// (integer division), <c>ReplenishmentPeriod = 1 s</c> — so waits are short and steady
/// instead of a once-a-minute thundering herd. <c>TokenLimit</c> is the full per-minute
/// budget, letting an idle limiter absorb a burst of up to one minute's worth of calls.
/// Two consequences worth knowing: budgets below 60 rpm replenish at the 1-token-per-second
/// floor, so their sustained rate can exceed the configured budget (bursts stay bounded by
/// <c>TokenLimit</c>); budgets that are not a multiple of 60 floor to the next lower
/// per-second rate for sustained traffic.
/// </remarks>
public sealed class TokenBucketRateLimiterAdapter : IRateLimiter
{
    private readonly RateLimiter _limiter;
    private readonly KeyValuePair<string, object?> _surfaceTag;

    /// <summary>Creates an auto-replenishing limiter from a per-minute budget.</summary>
    /// <param name="requestsPerMinute">Sustained request budget per minute; must be positive.</param>
    /// <param name="surface">Telemetry surface tag, e.g. <c>"chat"</c> or <c>"embedding"</c>.</param>
    /// <param name="maxQueuedRequests">
    /// Maximum callers allowed to wait; further calls throw <see cref="InvalidOperationException"/>
    /// instead of waiting. <see langword="null"/> (default) means an unbounded queue.
    /// </param>
    public TokenBucketRateLimiterAdapter(int requestsPerMinute, string surface, int? maxQueuedRequests = null)
        : this(CreateLimiter(requestsPerMinute, surface, maxQueuedRequests), surface)
    {
    }

    /// <summary>
    /// Validates all arguments before constructing the limiter so an invalid call cannot
    /// leak a live auto-replenishment timer.
    /// </summary>
    private static TokenBucketRateLimiter CreateLimiter(int requestsPerMinute, string surface, int? maxQueuedRequests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        return new TokenBucketRateLimiter(CreateBucketOptions(requestsPerMinute, maxQueuedRequests));
    }

    /// <summary>
    /// Test seam: wraps a pre-built limiter (e.g. a manual-replenishment
    /// <see cref="TokenBucketRateLimiter"/>) for deterministic tests. The adapter owns and
    /// disposes <paramref name="limiter"/>.
    /// </summary>
    internal TokenBucketRateLimiterAdapter(RateLimiter limiter, string surface)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);

        _limiter = limiter;
        _surfaceTag = new KeyValuePair<string, object?>("surface", surface);
    }

    /// <summary>Derives the token-bucket options from a per-minute budget (see class remarks).</summary>
    internal static TokenBucketRateLimiterOptions CreateBucketOptions(int requestsPerMinute, int? maxQueuedRequests)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerMinute);
        if (maxQueuedRequests is { } maxQueued)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueued, nameof(maxQueuedRequests));
        }

        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = requestsPerMinute,
            TokensPerPeriod = Math.Max(1, requestsPerMinute / 60),
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = maxQueuedRequests ?? int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        };
    }

    /// <inheritdoc/>
    public async ValueTask AcquireAsync(int permits = 1, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var lease = await _limiter.AcquireAsync(permits, cancellationToken).ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException(
                    "Rate-limiter wait queue is full; the call was rejected instead of queued. " +
                    "Raise (or unset, for an unbounded queue) RateLimitingOptions.MaxQueuedRequests, " +
                    "increase the requests-per-minute budget, or reduce concurrent callers.");
            }
        }
        finally
        {
            // Wait time is recorded for every outcome (granted, rejected, cancelled): it is
            // the time a caller spent held at the limiter either way.
            RagTelemetry.RateLimitWaitDuration.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, _surfaceTag);
        }
    }

    /// <summary>Disposes the wrapped limiter (owned by this adapter).</summary>
    public void Dispose() => _limiter.Dispose();
}
