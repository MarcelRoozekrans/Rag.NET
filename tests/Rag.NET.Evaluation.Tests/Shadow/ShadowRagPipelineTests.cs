using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Shadow;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The decorator, and the isolation contract that is this phase's whole point. The tests pin the
/// structure, not just the outcome: the primary's task is already complete at the call site — so
/// nothing on the path awaited anything incomplete — and the decorator never invokes the
/// secondary at all, so no secondary behaviour can possibly couple to the caller. A
/// <c>try/catch</c> around an awaited secondary passes every "does it throw?" test and fails
/// these.
/// </summary>
[Collection("ShadowTelemetry")] // emits shadow counters; see ShadowTelemetryCollection
public sealed class ShadowRagPipelineTests
{
    /// <summary>
    /// Property 2, pinned the way the queue tests pin non-blocking: the secondary's task can
    /// never complete, so a decorator that awaited it could never return — asserting the ask is
    /// already complete at the call site fails fast instead of hanging, and proves the path
    /// awaited nothing incomplete. The call count pins the stronger structural fact: the
    /// decorator did not run the secondary at all.
    /// </summary>
    [Fact]
    public async Task AskAsync_ASecondaryThatNeverCompletes_DoesNotDelayThePrimary()
    {
        var secondary = new ScriptedRagPipeline((_, _, _) =>
            new TaskCompletionSource<RagResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var queue = NewQueue();
        var shadowed = NewShadowed(PrimaryAnswering(), secondary, queue);

        var ask = shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken);

        Assert.True(ask.IsCompletedSuccessfully);
        var response = await ask;
        Assert.Equal("primary-answer", response.Answer);
        Assert.Equal(0, secondary.AskCalls);
        Assert.Equal(1, queue.PendingCount);
    }

    /// <summary>
    /// Property 1: what is scheduled observes a completed primary — the queued item carries the
    /// primary's finished answer, its context, and a measured latency — and hands the consumer
    /// everything it needs to run the secondary later: the same question, the caller's options,
    /// the pipeline itself, and the label to capture it under.
    /// </summary>
    [Fact]
    public async Task AskAsync_EnqueuesTheCompletedPrimaryAndEverythingTheConsumerNeeds()
    {
        var secondary = new ScriptedRagPipeline();
        var queue = NewQueue();
        var callerOptions = new RagOptions();
        var capturedAt = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var shadowed = new ShadowRagPipeline(
            PrimaryAnswering(), secondary, queue, new ShadowPipelineOptions { SampleRate = 1.0 },
            new FixedTimeProvider(capturedAt));

        await shadowed.AskAsync("q", callerOptions, TestContext.Current.CancellationToken);

        Assert.True(queue.TryDequeue(out var pending));
        Assert.Equal("q", pending.Question);
        Assert.Same(callerOptions, pending.Options);
        Assert.Equal("primary", pending.Primary.VariantName);
        Assert.Equal("primary-answer", pending.Primary.Answer);
        Assert.Equal(["primary-context"], pending.Primary.ContextTexts);
        Assert.NotNull(pending.Primary.Latency);
        Assert.Null(pending.Primary.Failure);
        Assert.Same(secondary, pending.SecondaryPipeline);
        Assert.Equal("shadow", pending.SecondaryVariantName);
        Assert.Equal(capturedAt, pending.CapturedAt);
        Assert.Equal(0, secondary.AskCalls);
    }

    /// <summary>
    /// Required test 1, end to end: a secondary that throws does not affect the primary — the
    /// caller is served normally — and the thrown secondary arrives in the store as a recorded
    /// failure inside a persisted pair, because a secondary that threw is a result.
    /// </summary>
    [Fact]
    public async Task AskAsync_ASecondaryThatThrows_DoesNotAffectThePrimary_AndIsCapturedAsAFailure()
    {
        var secondary = new ScriptedRagPipeline((_, _, _) =>
            throw new InvalidOperationException("secondary broke"));
        var queue = NewQueue();
        var store = new InMemoryShadowCaptureStore();
        var shadowed = NewShadowed(PrimaryAnswering(), secondary, queue);
        using var consumer = NewConsumer(queue, store);
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var response = await shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("primary-answer", response.Answer);
        var capture = Assert.Single(store.Snapshot());
        Assert.NotNull(capture.Secondary.Failure);
        Assert.Equal("InvalidOperationException: secondary broke", capture.Secondary.Failure.Message);
    }

    /// <summary>
    /// Required test 3: a full queue costs the sample — dropped and counted — never the caller.
    /// No consumer is attached, so the ask could never complete if anything on the path waited
    /// for queue space.
    /// </summary>
    [Fact]
    public async Task AskAsync_AFullQueue_NeitherDelaysNorFailsThePrimary()
    {
        var secondary = new ScriptedRagPipeline();
        var queue = NewQueue(capacity: 1);
        var shadowed = NewShadowed(PrimaryAnswering(), secondary, queue);
        await shadowed.AskAsync("fills-the-queue", options: null, TestContext.Current.CancellationToken);

        var ask = shadowed.AskAsync("dropped", options: null, TestContext.Current.CancellationToken);

        Assert.True(ask.IsCompletedSuccessfully);
        var response = await ask;
        Assert.Equal("primary-answer", response.Answer);
        Assert.Equal(1, queue.DroppedCount);
    }

    /// <summary>
    /// Required test 4, end to end: the store is the consumer's problem, never the caller's —
    /// the primary's answer comes back, and the loss is recorded as a persistence failure.
    /// </summary>
    [Fact]
    public async Task AskAsync_AStoreThatThrows_StillReturnsThePrimaryAnswer()
    {
        var queue = NewQueue();
        var shadowed = NewShadowed(PrimaryAnswering(), new ScriptedRagPipeline(), queue);
        using var consumer = NewConsumer(queue, new ThrowingStore());
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        var response = await shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("primary-answer", response.Answer);
        var failure = Assert.Single(consumer.FailureSnapshot());
        Assert.Equal("InvalidOperationException: the store is down", failure.Message);
    }

    /// <summary>
    /// Required test 5: shadowing must not swallow genuine primary failures — the caller sees
    /// exactly what the primary threw, and nothing is scheduled, because there is no served
    /// answer to pair a secondary against.
    /// </summary>
    [Fact]
    public async Task AskAsync_APrimaryThatThrows_StillThrows()
    {
        var secondary = new ScriptedRagPipeline();
        var queue = NewQueue();
        var shadowed = NewShadowed(
            new ScriptedRagPipeline((_, _, _) => throw new InvalidOperationException("primary broke")),
            secondary,
            queue);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shadowed.AskAsync("q", options: null, TestContext.Current.CancellationToken));

        Assert.Equal("primary broke", thrown.Message);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.DroppedCount);
        Assert.Equal(0, secondary.AskCalls);
    }

    /// <summary>
    /// Required test 6: everything that is not <c>AskAsync</c> is straight delegation to the
    /// primary. The secondary is a <see cref="ScriptedRagPipeline"/>, whose non-Ask members
    /// throw — so a decorator that touched it here would fail loudly.
    /// </summary>
    [Fact]
    public async Task NonAskMembers_DelegateToThePrimary_AndNeverTouchTheSecondary()
    {
        var primary = Substitute.For<IRagPipeline>();
        var queue = NewQueue();
        var shadowed = NewShadowed(primary, new ScriptedRagPipeline(), queue);
        using var document = new MemoryStream([1, 2, 3]);
        var metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "doc-1.txt" };
        var ingestionOptions = new IngestionOptions();
        var retrievalOptions = new RetrievalOptions();
        var ragOptions = new RagOptions();

        _ = await shadowed.IngestAsync(
            document, metadata, ingestionOptions, progress: null, TestContext.Current.CancellationToken);
        _ = await shadowed.RetrieveAsync("find", retrievalOptions, TestContext.Current.CancellationToken);
        _ = shadowed.AskStreamingAsync("stream", ragOptions, TestContext.Current.CancellationToken);
        await shadowed.DeleteAsync("doc-1", TestContext.Current.CancellationToken);

        _ = await primary.Received(1).IngestAsync(
            document, metadata, ingestionOptions, null, TestContext.Current.CancellationToken);
        _ = await primary.Received(1).RetrieveAsync(
            "find", retrievalOptions, TestContext.Current.CancellationToken);
        _ = primary.Received(1).AskStreamingAsync(
            "stream", ragOptions, TestContext.Current.CancellationToken);
        await primary.Received(1).DeleteAsync("doc-1", TestContext.Current.CancellationToken);
        // Only AskAsync is shadowed: nothing above put work on the queue.
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var primary = new ScriptedRagPipeline();
        var secondary = new ScriptedRagPipeline();
        var queue = NewQueue();
        var options = new ShadowPipelineOptions();

        Assert.Throws<ArgumentNullException>(() => new ShadowRagPipeline(null!, secondary, queue, options));
        Assert.Throws<ArgumentNullException>(() => new ShadowRagPipeline(primary, null!, queue, options));
        Assert.Throws<ArgumentNullException>(() => new ShadowRagPipeline(primary, secondary, null!, options));
        Assert.Throws<ArgumentNullException>(() => new ShadowRagPipeline(primary, secondary, queue, null!));
    }

    [Fact]
    public void Constructor_IdenticalVariantNames_IsRejected()
    {
        // Same-named sides make every capture unscoreable; refused where the decorator is built,
        // not on the consumer after production traffic already flowed through it.
        Assert.Throws<ArgumentException>(() => new ShadowRagPipeline(
            new ScriptedRagPipeline(), new ScriptedRagPipeline(), NewQueue(),
            new ShadowPipelineOptions { PrimaryVariantName = "same", SecondaryVariantName = "same" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_BlankVariantNames_AreRejected(string blank)
    {
        Assert.Throws<ArgumentException>(() => new ShadowPipelineOptions { PrimaryVariantName = blank });
        Assert.Throws<ArgumentException>(() => new ShadowPipelineOptions { SecondaryVariantName = blank });
    }

    [Fact]
    public void Options_Defaults_NameTheSidesPrimaryAndShadow()
    {
        var options = new ShadowPipelineOptions();

        Assert.Equal("primary", options.PrimaryVariantName);
        Assert.Equal("shadow", options.SecondaryVariantName);
    }

    private static ShadowCaptureQueue NewQueue(int capacity = 8) =>
        new(new ShadowCaptureQueueOptions { Capacity = capacity });

    /// <summary>
    /// Rate 1 — the isolation-contract tests are about what happens when a request IS shadowed,
    /// and sampling (including the off-by-default rate 0) has its own suite in
    /// <see cref="ShadowSamplingTests"/>.
    /// </summary>
    private static ShadowRagPipeline NewShadowed(
        IRagPipeline primary,
        IRagPipeline secondary,
        ShadowCaptureQueue queue) =>
        new(primary, secondary, queue, new ShadowPipelineOptions { SampleRate = 1.0 });

    /// <summary>
    /// The drain timeout mirrors the consumer tests: generous enough that a healthy drain never
    /// meets it, so no passing test ever waits on it.
    /// </summary>
    private static ShadowCaptureConsumer NewConsumer(ShadowCaptureQueue queue, IShadowCaptureStore store) =>
        new(queue, store, new ShadowCaptureConsumerOptions { DrainTimeout = TimeSpan.FromMinutes(1) });

    /// <summary>A primary that completes synchronously — the ask task's completion state at the
    /// call site then reveals whether the decorator awaited anything incomplete after it.</summary>
    private static ScriptedRagPipeline PrimaryAnswering() => new((_, _, _) =>
        Task.FromResult(ScriptedRagPipeline.Response("primary-answer", "primary-context")));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingStore : IShadowCaptureStore
    {
        public Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the store is down");
    }
}
