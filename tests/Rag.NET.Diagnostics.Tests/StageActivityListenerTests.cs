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
[Collection(ActivityCollection.Name)]
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

        activity.Stop();

        // Read from the buffer rather than from Current: a root pipeline span is the outermost one,
        // so stopping it commits the trace and takes it out of flight. That is the retrieve-only
        // path — nothing else would ever have committed it.
        var trace = Assert.Single(buffer.Snapshot());

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

        inner.Stop();

        // The inner span has a pipeline span for a parent, so it is not the end of anything and must
        // not commit. Were it to, the outer stage would land in a second trace.
        Assert.Empty(buffer.Snapshot());

        outer.Stop();

        var trace = Assert.Single(buffer.Snapshot());

        string[] expected = ["ragnet.embed", "ragnet.retrieve"];
        Assert.Equal(expected, trace.Stages.Select(s => s.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void AForeignSpanBetweenTwoPipelineSpans_DoesNotSplitTheTrace()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        using var listener = new StageActivityListener(collector);
        using var source = new ActivitySource(RagNetSourceName);

        using var foreignSource = new ActivitySource(UnrelatedSourceName);
        using var keepAlive = AllDataListenerFor(UnrelatedSourceName);

        // What a third-party instrumented IRetriever or IChatClient decorator does: it opens a span of
        // its own between the pipeline's, so ragnet.retrieve's immediate parent is not a pipeline span
        // even though ragnet.query is still above it and still owns the commit.
        var outer = source.StartActivity("ragnet.query");
        Assert.NotNull(outer);

        var foreign = foreignSource.StartActivity("SomeLibrary.Decorate");
        Assert.NotNull(foreign);

        var inner = source.StartActivity("ragnet.retrieve");
        Assert.NotNull(inner);
        Assert.Equal(outer.TraceId, inner.TraceId);

        inner.Stop();

        // Reading only the immediate parent, this committed here: a trace holding ragnet.retrieve and
        // nothing else, with the query it belongs to still in flight.
        Assert.Empty(buffer.Snapshot());

        foreign.Stop();
        outer.Stop();

        // One trace rather than two, and both stages in it. Two would have shared the outer span's
        // trace id, so a newest-wins fetch by that id could only ever have returned the second.
        var trace = Assert.Single(buffer.Snapshot());

        string[] expected = ["ragnet.retrieve", "ragnet.query"];
        Assert.Equal(expected, trace.Stages.Select(s => s.Name), StringComparer.Ordinal);
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

        // Both halves, and the second is not redundant. Now that stopping an outermost pipeline span
        // commits, "Current is null" no longer means "nothing was recorded" on its own — a span that
        // was wrongly recorded would have been committed straight out of flight and left Current null
        // too. The buffer is where a wrongly-recorded span would show up.
        Assert.Null(collector.Current(traceId));
        Assert.Empty(buffer.Snapshot());
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
        // though it were one. Checked in the buffer as well — see SpansFromAnotherSource_AreNotRecorded.
        Assert.Null(collector.Current(traceId));
        Assert.Empty(buffer.Snapshot());
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

        // See SpansFromAnotherSource_AreNotRecorded for why the buffer is checked too.
        Assert.Null(collector.Current(traceId));
        Assert.Empty(buffer.Snapshot());
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
