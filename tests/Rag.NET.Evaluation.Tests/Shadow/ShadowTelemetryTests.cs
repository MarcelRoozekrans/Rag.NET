using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Rag.NET.Evaluation.Shadow;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The counters an operator actually reads. The queue's and consumer's own properties
/// (<c>DroppedCount</c>, <c>AbandonedCount</c>, <c>FailureSnapshot</c>) are exact but local to
/// the instance; these tests pin that every loss and every success also lands on the meter,
/// where dashboards can see it — a counter nobody can read is not observability. The identity
/// the instruments are named for: <c>enqueued − dropped − failed − abandoned = persisted</c>.
/// </summary>
[Collection("ShadowTelemetry")]
public sealed class ShadowTelemetryTests
{
    [Fact]
    public async Task Enqueue_CountsEveryOffer_AndCountsTheDropWhenTheQueueIsFull()
    {
        using var enqueued = Collector("ragnet.shadow.enqueued");
        using var dropped = Collector("ragnet.shadow.dropped");
        var enqueuedBefore = Sum(enqueued);
        var droppedBefore = Sum(dropped);
        var queue = NewQueue(capacity: 1);

        await queue.EnqueueAsync(Pending("kept"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Pending("dropped"), TestContext.Current.CancellationToken);

        Assert.Equal(2, Sum(enqueued) - enqueuedBefore);
        Assert.Equal(1, Sum(dropped) - droppedBefore);
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public async Task Enqueue_AfterComplete_CountsTheRefusalAsADrop()
    {
        using var dropped = Collector("ragnet.shadow.dropped");
        var droppedBefore = Sum(dropped);
        var queue = NewQueue(capacity: 8);
        queue.Complete();

        await queue.EnqueueAsync(Pending("refused"), TestContext.Current.CancellationToken);

        Assert.Equal(1, Sum(dropped) - droppedBefore);
    }

    [Fact]
    public async Task Consumer_CountsPersistedCapturesAsProcessed_AndStoreRefusalsAsFailed()
    {
        using var processed = Collector("ragnet.shadow.processed");
        using var failed = Collector("ragnet.shadow.failed");
        var processedBefore = Sum(processed);
        var failedBefore = Sum(failed);
        var queue = NewQueue(capacity: 8);
        var store = new PoisonStore();
        using var consumer = new ShadowCaptureConsumer(queue, store, new ShadowCaptureConsumerOptions
        {
            DrainTimeout = TimeSpan.FromMinutes(1),
        });
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending("poison"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Pending("survivor"), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Sum(processed) - processedBefore);
        Assert.Equal(1, Sum(failed) - failedBefore);
    }

    [Fact]
    public async Task Consumer_CountsShutdownAbandonment()
    {
        using var abandoned = Collector("ragnet.shadow.abandoned");
        var abandonedBefore = Sum(abandoned);
        var queue = NewQueue(capacity: 8);
        var secondaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = new ScriptedRagPipeline((_, _, _) =>
        {
            secondaryStarted.TrySetResult();
            return new TaskCompletionSource<RagResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        });
        using var consumer = new ShadowCaptureConsumer(queue, new PoisonStore(), new ShadowCaptureConsumerOptions
        {
            DrainTimeout = TimeSpan.Zero,
        });
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending("in-flight", stuck), TestContext.Current.CancellationToken);
        await secondaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(Pending("queued", stuck), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Sum(abandoned) - abandonedBefore);
        Assert.Equal(2, consumer.AbandonedCount);
    }

    private static MetricCollector<long> Collector(string instrument) =>
        new(ShadowTelemetry.Meter, instrument);

    private static long Sum(MetricCollector<long> collector)
    {
        long total = 0;
        foreach (var measurement in collector.GetMeasurementSnapshot())
        {
            total += measurement.Value;
        }

        return total;
    }

    private static ShadowCaptureQueue NewQueue(int capacity) =>
        new(new ShadowCaptureQueueOptions { Capacity = capacity });

    private static PendingShadowCapture Pending(string question, ScriptedRagPipeline? secondary = null) => new(
        question,
        Options: null,
        new ShadowVariantCapture("primary", Answer: "a", ContextTexts: ["c"], Latency: TimeSpan.FromMilliseconds(1)),
        secondary ?? new ScriptedRagPipeline(),
        "shadow",
        new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Refuses captures whose question is "poison"; accepts everything else.</summary>
    private sealed class PoisonStore : IShadowCaptureStore
    {
        public Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return string.Equals(capture.Question, "poison", StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("the store rejected it"))
                : Task.CompletedTask;
        }
    }
}
