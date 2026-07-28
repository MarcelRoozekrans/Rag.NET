using System.Diagnostics;
using Rag.NET.Diagnostics.Internal;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The latency breakdown is a subscription to spans that already exist, so what these tests pin is
/// the subscription: which spans it takes, which it leaves alone, and that it stops when disposed.
/// </summary>
/// <remarks>
/// Every test asserts the <see cref="Activity"/> was actually created before asserting on what was
/// recorded. Without a sampling listener <c>StartActivity</c> returns <see langword="null"/>, and a
/// test that then checks "nothing was recorded" passes while proving nothing at all — including the
/// tests whose whole point is that nothing was recorded.
/// </remarks>
public sealed class StageActivityListenerTests
{
    /// <summary>The name the pipeline's spans are emitted under; see <c>RagTelemetry.SourceName</c>.</summary>
    private const string RagNetSourceName = "Rag.NET";

    private const string UnrelatedSourceName = "Some.Other.Library";

    [Fact]
    public void AStoppedStageSpan_IsRecordedWithItsNameAndDuration()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        using var listener = new StageActivityListener(collector);
        using var source = new ActivitySource(RagNetSourceName);

        var activity = source.StartActivity("ragnet.retrieve");
        Assert.NotNull(activity);

        var traceId = activity.TraceId.ToHexString();
        activity.Stop();

        var trace = collector.Current(traceId);
        Assert.NotNull(trace);

        var stage = Assert.Single(trace.Stages);
        Assert.Equal("ragnet.retrieve", stage.Name);
        Assert.True(stage.Duration >= TimeSpan.Zero);
        Assert.NotEqual(default, stage.StartedAt);
    }

    [Fact]
    public void NestedStageSpans_JoinIntoOneTraceOnTheSharedTraceId()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        using var listener = new StageActivityListener(collector);
        using var source = new ActivitySource(RagNetSourceName);

        var outer = source.StartActivity("ragnet.retrieve");
        Assert.NotNull(outer);

        var inner = source.StartActivity("ragnet.embed");
        Assert.NotNull(inner);

        // The join is the whole reason spans are used rather than a stopwatch: a child span inherits
        // its parent's trace id, so the stages of one query assemble themselves.
        Assert.Equal(outer.TraceId, inner.TraceId);

        var traceId = outer.TraceId.ToHexString();
        inner.Stop();
        outer.Stop();

        var trace = collector.Current(traceId);
        Assert.NotNull(trace);

        string[] expected = ["ragnet.embed", "ragnet.retrieve"];
        Assert.Equal(expected, trace.Stages.Select(s => s.Name));
    }

    [Fact]
    public void SpansFromAnotherSource_AreNotRecorded()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        using var listener = new StageActivityListener(collector);

        using var unrelatedSource = new ActivitySource(UnrelatedSourceName);
        using var keepAlive = AllDataListenerFor(UnrelatedSourceName);

        // Named exactly like a pipeline stage, so only the source filter can keep it out. A listener
        // that quietly recorded every library's spans would fill the buffer with somebody else's work.
        var activity = unrelatedSource.StartActivity("ragnet.retrieve");
        Assert.NotNull(activity);

        var traceId = activity.TraceId.ToHexString();
        activity.Stop();

        Assert.Null(collector.Current(traceId));
    }

    [Fact]
    public void NonStageSpansFromTheRagNetSource_AreNotRecorded()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        using var listener = new StageActivityListener(collector);
        using var source = new ActivitySource(RagNetSourceName);

        var activity = source.StartActivity("something.else");
        Assert.NotNull(activity);

        var traceId = activity.TraceId.ToHexString();
        activity.Stop();

        // The source is shared. A span that is not a pipeline stage must not appear in a trace as
        // though it were one.
        Assert.Null(collector.Current(traceId));
    }

    [Fact]
    public void AfterDispose_SpansAreNoLongerRecorded()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var listener = new StageActivityListener(collector);
        listener.Dispose();

        using var source = new ActivitySource(RagNetSourceName);

        // Something else has to keep sampling alive, or StartActivity returns null and this passes
        // for the wrong reason — proving only that an unsampled span is not recorded.
        using var keepAlive = AllDataListenerFor(RagNetSourceName);

        var activity = source.StartActivity("ragnet.ask");
        Assert.NotNull(activity);

        var traceId = activity.TraceId.ToHexString();
        activity.Stop();

        Assert.Null(collector.Current(traceId));
    }

    /// <summary>A listener that samples everything from one source, so its spans are really created.</summary>
    /// <param name="sourceName">The source to sample.</param>
    /// <returns>The subscribed listener; dispose it to unsubscribe.</returns>
    private static ActivityListener AllDataListenerFor(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }
}
