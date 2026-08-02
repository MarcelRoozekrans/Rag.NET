using System.Collections.Concurrent;
using Rag.NET.Evaluation.Shadow;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The background consumer between the queue and the store. The property worth pinning is the
/// shutdown contract: captures the queue already accepted are drained rather than abandoned, the
/// drain is bounded rather than open-ended, and whatever the bound cost is reported rather than
/// lost silently — silent abandonment is the fire-and-forget loss this phase exists to close, and
/// it is exactly what the neighbouring ingestion processor's clean-exit-on-cancel shutdown does.
/// </summary>
public sealed class ShadowCaptureConsumerTests
{
    /// <summary>
    /// The test the neighbouring pattern fails. The store's first save is gated open only after
    /// shutdown has begun, so two captures are provably still queued when stop is requested — a
    /// consumer that treats the stopping token as a clean exit abandons both.
    /// </summary>
    [Fact]
    public async Task StopAsync_CapturesStillQueuedAtShutdown_AreAllPersisted()
    {
        var queue = NewQueue();
        var firstSaveStarted = NewSignal();
        var release = NewSignal();
        var first = 0;
        var store = new ScriptedStore(async (_, _) =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                firstSaveStarted.TrySetResult();
                await release.Task;
            }
        });
        using var consumer = NewConsumer(queue, store);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Capture("in-flight"), TestContext.Current.CancellationToken);
        await firstSaveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("queued-1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("queued-2"), TestContext.Current.CancellationToken);

        // Stop while the consumer is provably mid-save and two captures are provably buffered,
        // then let the save finish. Only a real drain persists the buffered two.
        var stop = consumer.StopAsync(CancellationToken.None);
        release.TrySetResult();
        await stop.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["in-flight", "queued-1", "queued-2"], store.SavedQuestions);
        Assert.Equal(0, consumer.AbandonedCount);
    }

    /// <summary>
    /// A store that never completes a save cannot be drained; the deadline (zero here — the
    /// controllable stand-in for "expired") must end the drain and the remainder must be
    /// reported: the in-flight capture plus the two still queued.
    /// </summary>
    [Fact]
    public async Task StopAsync_ADrainThatCannotFinishInTime_ReportsTheRemainder()
    {
        var queue = NewQueue();
        var firstSaveStarted = NewSignal();
        var store = new ScriptedStore((_, cancellationToken) =>
        {
            firstSaveStarted.TrySetResult();
            return Never(cancellationToken);
        });
        using var consumer = NewConsumer(queue, store, drainTimeout: TimeSpan.Zero);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Capture("in-flight"), TestContext.Current.CancellationToken);
        await firstSaveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("queued-1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("queued-2"), TestContext.Current.CancellationToken);

        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, consumer.AbandonedCount);
        Assert.Empty(store.SavedQuestions);
    }

    [Fact]
    public async Task Consumer_APoisonedCapture_IsRecordedAsAFailureAndLaterCapturesStillPersist()
    {
        var queue = NewQueue();
        var store = new ScriptedStore((capture, _) =>
            string.Equals(capture.Question, "poison", StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("the store rejected it"))
                : Task.CompletedTask);
        using var consumer = NewConsumer(queue, store);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Capture("poison"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("survivor"), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["survivor"], store.SavedQuestions);
        var failure = Assert.Single(consumer.FailureSnapshot());
        Assert.Equal("poison", failure.Question);
        Assert.Equal("InvalidOperationException: the store rejected it", failure.Message);
        // Failed is not abandoned: the capture was dequeued and offered to the store in time.
        Assert.Equal(0, consumer.AbandonedCount);
    }

    [Fact]
    public async Task Consumer_SteadyState_PersistsEnqueuedCapturesToTheStore()
    {
        var queue = NewQueue();
        var store = new ScriptedStore(signalAfter: 2);
        using var consumer = NewConsumer(queue, store);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Capture("q1"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Capture("q2"), TestContext.Current.CancellationToken);
        await store.Signal.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["q1", "q2"], store.SavedQuestions);

        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, consumer.AbandonedCount);
    }

    [Fact]
    public async Task StopAsync_NothingQueued_ReportsNothingLost()
    {
        var queue = NewQueue();
        var store = new ScriptedStore();
        using var consumer = NewConsumer(queue, store);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, consumer.AbandonedCount);
        Assert.Empty(consumer.FailureSnapshot());
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var queue = NewQueue();
        var store = new ScriptedStore();
        var options = new ShadowCaptureConsumerOptions();

        Assert.Throws<ArgumentNullException>(() => new ShadowCaptureConsumer(null!, store, options));
        Assert.Throws<ArgumentNullException>(() => new ShadowCaptureConsumer(queue, null!, options));
        Assert.Throws<ArgumentNullException>(() => new ShadowCaptureConsumer(queue, store, null!));
    }

    [Fact]
    public void Options_NegativeDrainTimeout_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShadowCaptureConsumerOptions { DrainTimeout = TimeSpan.FromSeconds(-1) });
    }

    [Fact]
    public void Options_DefaultDrainTimeout_IsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new ShadowCaptureConsumerOptions().DrainTimeout);
    }

    [Fact]
    public void PersistenceFailure_FromException_KeepsTheTypeNameAndMessage()
    {
        var failure = ShadowPersistenceFailure.FromException(
            "q", new InvalidOperationException("boom"));

        Assert.Equal("q", failure.Question);
        Assert.Equal("InvalidOperationException: boom", failure.Message);
    }

    [Fact]
    public void PersistenceFailure_FromNullException_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShadowPersistenceFailure.FromException("q", null!));
    }

    private static ShadowCaptureQueue NewQueue() =>
        new(new ShadowCaptureQueueOptions { Capacity = 8 });

    /// <summary>
    /// The default drain timeout is a minute: generous enough that a healthy drain never meets
    /// it, so no passing test ever waits on it — the deadline tests override it with zero.
    /// </summary>
    private static ShadowCaptureConsumer NewConsumer(
        ShadowCaptureQueue queue,
        IShadowCaptureStore store,
        TimeSpan? drainTimeout = null) =>
        new(queue, store, new ShadowCaptureConsumerOptions
        {
            DrainTimeout = drainTimeout ?? TimeSpan.FromMinutes(1),
        });

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A save that never completes on its own — only the token can end it.</summary>
    private static Task Never(CancellationToken cancellationToken)
    {
        var signal = NewSignal();
        cancellationToken.Register(() => signal.TrySetCanceled(cancellationToken));
        return signal.Task;
    }

    private static ShadowCapture Capture(string question) => new(
        question,
        new ShadowVariantCapture("primary", Answer: "a", ContextTexts: ["c"]),
        new ShadowVariantCapture("shadow", Answer: "b", ContextTexts: ["d"]),
        new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// A store whose per-save behaviour the test scripts. Like the real
    /// <see cref="InMemoryShadowCaptureStore"/>, it observes its cancellation token before doing
    /// anything — the shutdown tests rely on that to prove which token the consumer hands it.
    /// </summary>
    private sealed class ScriptedStore(
        Func<ShadowCapture, CancellationToken, Task>? script = null,
        int signalAfter = int.MaxValue) : IShadowCaptureStore
    {
        private readonly Func<ShadowCapture, CancellationToken, Task> _script =
            script ?? ((_, _) => Task.CompletedTask);

        private readonly ConcurrentQueue<ShadowCapture> _saved = new();
        private readonly TaskCompletionSource _signal = NewSignal();
        private int _savedCount;

        /// <summary>Completes once <paramref name="signalAfter"/> saves have been recorded.</summary>
        public Task Signal => _signal.Task;

        public IReadOnlyList<string> SavedQuestions
        {
            get
            {
                var questions = new List<string>();
                foreach (var capture in _saved)
                {
                    questions.Add(capture.Question);
                }

                return questions;
            }
        }

        public async Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _script(capture, cancellationToken);
            _saved.Enqueue(capture);
            if (Interlocked.Increment(ref _savedCount) >= signalAfter)
            {
                _signal.TrySetResult();
            }
        }
    }
}
