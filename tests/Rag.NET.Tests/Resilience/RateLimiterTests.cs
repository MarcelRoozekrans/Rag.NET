using System.Threading.RateLimiting;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

public class RateLimiterTests
{
    /// <summary>
    /// Manual-replenishment bucket for deterministic waits: with <c>AutoReplenishment = false</c>
    /// nothing refills the bucket until the test calls <c>TryReplenish()</c>. TryReplenish is
    /// gated on an elapsed <c>ReplenishmentPeriod</c>, so the period is 1 Stopwatch tick (100 ns):
    /// by the time a test calls it, at least one tick has always passed and a single call refills.
    /// </summary>
    private static TokenBucketRateLimiter ManualBucket(int tokenLimit, int queueLimit) => new(
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromTicks(1),
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false,
        });

    // ── Bucket-option derivation (pinned) ────────────────────────────────────

    [Theory]
    [InlineData(600, 10)] // spread evenly: 600 rpm → 10 tokens per 1-second period
    [InlineData(120, 2)]
    [InlineData(60, 1)]
    [InlineData(90, 1)]   // integer division floors partial tokens
    [InlineData(1, 1)]    // sub-60 budgets floor at 1 token/second (documented over-admission)
    public void CreateBucketOptions_TokensPerPeriod_IsRpmOver60FlooredAtOne(int rpm, int expectedTokensPerPeriod)
    {
        var options = TokenBucketRateLimiterAdapter.CreateBucketOptions(rpm, maxQueuedRequests: null);

        Assert.Equal(expectedTokensPerPeriod, options.TokensPerPeriod);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ReplenishmentPeriod);
    }

    [Fact]
    public void CreateBucketOptions_BucketCapacityIsFullPerMinuteBudget()
    {
        var options = TokenBucketRateLimiterAdapter.CreateBucketOptions(240, maxQueuedRequests: null);

        Assert.Equal(240, options.TokenLimit); // an idle limiter can absorb a burst of one minute's budget
        Assert.True(options.AutoReplenishment);
        Assert.Equal(QueueProcessingOrder.OldestFirst, options.QueueProcessingOrder);
    }

    [Fact]
    public void CreateBucketOptions_NoMaxQueuedRequests_QueueIsUnbounded()
    {
        var options = TokenBucketRateLimiterAdapter.CreateBucketOptions(60, maxQueuedRequests: null);

        Assert.Equal(int.MaxValue, options.QueueLimit);
    }

    [Fact]
    public void CreateBucketOptions_MaxQueuedRequests_BoundsTheQueue()
    {
        var options = TokenBucketRateLimiterAdapter.CreateBucketOptions(60, maxQueuedRequests: 5);

        Assert.Equal(5, options.QueueLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateBucketOptions_NonPositiveRpm_Throws(int rpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TokenBucketRateLimiterAdapter.CreateBucketOptions(rpm, maxQueuedRequests: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateBucketOptions_NonPositiveMaxQueuedRequests_Throws(int maxQueued)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TokenBucketRateLimiterAdapter.CreateBucketOptions(60, maxQueued));
    }

    [Fact]
    public void Ctor_BlankSurface_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TokenBucketRateLimiterAdapter(ManualBucket(1, 0), " "));
    }

    // ── Waiting semantics (deterministic via manual replenish) ───────────────

    [Fact]
    public async Task AcquireAsync_TokenAvailable_CompletesImmediately()
    {
        using var adapter = new TokenBucketRateLimiterAdapter(ManualBucket(tokenLimit: 1, queueLimit: 0), "chat");

        await adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AcquireAsync_BucketEmpty_WaitsUntilReplenish()
    {
        var bucket = ManualBucket(tokenLimit: 1, queueLimit: 10);
        using var adapter = new TokenBucketRateLimiterAdapter(bucket, "chat");
        await adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);

        var second = adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask();
        Assert.False(second.IsCompleted); // nothing can replenish the manual bucket on its own

        bucket.TryReplenish();
        await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileWaiting_ThrowsOce()
    {
        var bucket = ManualBucket(tokenLimit: 1, queueLimit: 10);
        using var adapter = new TokenBucketRateLimiterAdapter(bucket, "chat");
        await adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = adapter.AcquireAsync(cancellationToken: cts.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcquireAsync_QueueFull_ThrowsInvalidOperationWithGuidance()
    {
        using var adapter = new TokenBucketRateLimiterAdapter(ManualBucket(tokenLimit: 1, queueLimit: 0), "chat");
        await adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("MaxQueuedRequests", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_DisposesInnerLimiter()
    {
        var adapter = new TokenBucketRateLimiterAdapter(ManualBucket(tokenLimit: 1, queueLimit: 0), "chat");
        adapter.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }
}
