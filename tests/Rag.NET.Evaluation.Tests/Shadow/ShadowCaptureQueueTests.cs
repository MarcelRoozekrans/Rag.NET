using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The queue between the request path and the background consumer. The property worth pinning is
/// the isolation contract: a full queue must never block the enqueuer, because blocking couples
/// the primary request's latency to the secondary's throughput — and every drop that isolation
/// costs must be counted exactly, or the real capture rate falls quietly below the configured
/// sample rate.
/// </summary>
public sealed class ShadowCaptureQueueTests
{
    [Fact]
    public async Task EnqueueAsync_OnAFullQueue_IsAlreadyCompleteWhenItReturns()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 1 });
        await queue.EnqueueAsync(Capture("q1"), TestContext.Current.CancellationToken);

        // Nothing ever reads from this queue, so an enqueue that waited for space could never
        // finish. Asserting the task is already complete at the call site — rather than awaiting
        // it, which would prove only that it completes eventually — is what pins non-blocking.
        var enqueue = queue.EnqueueAsync(Capture("q2"), TestContext.Current.CancellationToken);

        Assert.True(enqueue.IsCompletedSuccessfully);
        await enqueue;
    }

    [Fact]
    public async Task EnqueueAsync_OnAFullQueue_CountsEveryDrop()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 2 });
        await queue.EnqueueAsync(Capture("kept-1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("kept-2"), TestContext.Current.CancellationToken);

        for (var i = 0; i < 3; i++)
        {
            // The completion guard keeps this test hang-proof: under a blocking queue the await
            // below would never return, so the guard fails fast instead of deadlocking the suite.
            var overflow = queue.EnqueueAsync(Capture($"dropped-{i}"), TestContext.Current.CancellationToken);
            Assert.True(overflow.IsCompletedSuccessfully);
            await overflow;
        }

        Assert.Equal(3, queue.DroppedCount);
    }

    [Fact]
    public async Task EnqueueAsync_OnAFullQueue_DropsTheIncomingCaptureAndKeepsTheQueued()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 2 });
        await queue.EnqueueAsync(Capture("kept-1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("kept-2"), TestContext.Current.CancellationToken);

        var overflow = queue.EnqueueAsync(Capture("dropped"), TestContext.Current.CancellationToken);
        Assert.True(overflow.IsCompletedSuccessfully);
        await overflow;

        // The incoming capture is the one lost — never a capture the queue already accepted. An
        // accepted capture that later vanished would be a second, uncountable kind of loss.
        var kept = await ReadAsync(queue, 2, TestContext.Current.CancellationToken);
        Assert.Equal(["kept-1", "kept-2"], kept);
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public async Task EnqueueAsync_ThenDequeueAllAsync_RoundTripsInOrderWithoutDrops()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 8 });
        await queue.EnqueueAsync(Capture("q1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("q2"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("q3"), TestContext.Current.CancellationToken);

        var read = await ReadAsync(queue, 3, TestContext.Current.CancellationToken);

        Assert.Equal(["q1", "q2", "q3"], read);
        Assert.Equal(0, queue.DroppedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_NonPositiveCapacity_IsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShadowCaptureQueueOptions { Capacity = capacity });
    }

    [Fact]
    public void Options_DefaultCapacity_MatchesTheIngestionQueuePrecedent()
    {
        Assert.Equal(1000, new ShadowCaptureQueueOptions().Capacity);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ShadowCaptureQueue(null!));
    }

    [Fact]
    public async Task Complete_ThenEnqueueAsync_CountsTheRefusedCaptureAsADrop()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 4 });
        queue.Complete();

        // Refused-after-Complete must stay non-blocking and must be counted: an uncounted
        // refusal at shutdown would be the same silent loss as an uncounted overflow drop.
        var enqueue = queue.EnqueueAsync(Capture("late"), TestContext.Current.CancellationToken);
        Assert.True(enqueue.IsCompletedSuccessfully);
        await enqueue;

        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public async Task Complete_DequeueAllAsyncEndsAfterDrainingTheBuffer()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 4 });
        await queue.EnqueueAsync(Capture("q1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("q2"), TestContext.Current.CancellationToken);
        queue.Complete();

        // Enumerated to natural completion, no break: the loop ending at all is the assertion
        // that Complete turns the stream finite once the buffered captures are drained.
        var read = new List<string>();
        await foreach (var capture in queue.DequeueAllAsync(TestContext.Current.CancellationToken))
        {
            read.Add(capture.Question);
        }

        Assert.Equal(["q1", "q2"], read);
    }

    [Fact]
    public async Task PendingCount_TracksAcceptedCapturesNotYetDequeued()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 4 });
        Assert.Equal(0, queue.PendingCount);

        await queue.EnqueueAsync(Capture("q1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("q2"), TestContext.Current.CancellationToken);
        Assert.Equal(2, queue.PendingCount);

        await ReadAsync(queue, 2, TestContext.Current.CancellationToken);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task TryDequeue_ReadsWithoutWaitingAndReportsAnEmptyQueue()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 4 });
        Assert.False(queue.TryDequeue(out _));

        await queue.EnqueueAsync(Capture("q1"), TestContext.Current.CancellationToken);

        Assert.True(queue.TryDequeue(out var capture));
        Assert.Equal("q1", capture.Question);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task EnqueueAsync_PreCancelledToken_IsCanceled()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 4 });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.EnqueueAsync(Capture("q"), cts.Token).AsTask());
        Assert.Equal(0, queue.DroppedCount);
    }

    [Fact]
    public async Task EnqueueAsync_NullCapture_Throws()
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 1 });

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => queue.EnqueueAsync(null!, TestContext.Current.CancellationToken).AsTask());
    }

    private static async Task<List<string>> ReadAsync(
        ShadowCaptureQueue queue,
        int count,
        CancellationToken cancellationToken)
    {
        var questions = new List<string>(count);
        await foreach (var capture in queue.DequeueAllAsync(cancellationToken))
        {
            questions.Add(capture.Question);
            if (questions.Count == count)
            {
                break;
            }
        }

        return questions;
    }

    private static ShadowCapture Capture(string question) => new(
        question,
        new ShadowVariantCapture("primary", Answer: "a", ContextTexts: ["c"]),
        new ShadowVariantCapture("shadow", Answer: "b", ContextTexts: ["d"]),
        new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
}
