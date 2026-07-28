using Rag.NET.Diagnostics.Internal;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The buffer is pure and bounded, so it pins exhaustively with no pipeline behind it — the same
/// reason the ring lands before anything that fills it.
/// </summary>
public sealed class TraceRingBufferTests
{
    [Fact]
    public void Add_BeyondCapacity_EvictsOldestFirst()
    {
        var buffer = new TraceRingBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
            buffer.Add(TraceWithId($"trace-{i}"));

        // Newest first, oldest evicted. A debugger is read immediately after the request,
        // so recency is the ordering that matters.
        string[] expected = ["trace-4", "trace-3", "trace-2"];
        Assert.Equal(expected, buffer.Snapshot().Select(t => t.TraceId));
    }

    [Fact]
    public void Add_BelowCapacity_KeepsEverythingNewestFirst()
    {
        var buffer = new TraceRingBuffer(capacity: 5);
        buffer.Add(TraceWithId("a"));
        buffer.Add(TraceWithId("b"));

        string[] expected = ["b", "a"];
        Assert.Equal(expected, buffer.Snapshot().Select(t => t.TraceId));
    }

    [Fact]
    public void Snapshot_OfAnEmptyBuffer_IsEmptyRatherThanCapacityNulls()
    {
        var buffer = new TraceRingBuffer(capacity: 3);

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Snapshot_IsAPointInTimeCopy_NotALiveView()
    {
        var buffer = new TraceRingBuffer(capacity: 3);
        buffer.Add(TraceWithId("a"));
        var snapshot = buffer.Snapshot();
        buffer.Add(TraceWithId("b"));

        // A reader iterating a snapshot must not observe concurrent writes mid-iteration.
        Assert.Single(snapshot);
    }

    [Fact]
    public async Task Add_UnderConcurrentWriters_NeverExceedsCapacityAndLosesNothingButTheEvicted()
    {
        var buffer = new TraceRingBuffer(capacity: 100);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(
            () =>
            {
                for (var i = 0; i < 250; i++)
                    buffer.Add(TraceWithId($"w{w}-{i}"));
            },
            cancellationToken)));

        var snapshot = buffer.Snapshot();

        Assert.Equal(100, snapshot.Count);

        // Every writer's ids are distinct, so a duplicate would mean two writers took the same slot
        // and one retained trace was silently overwritten by another rather than by eviction.
        Assert.Equal(100, snapshot.Select(t => t.TraceId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TryGet_ByTraceId_FindsARetainedTrace()
    {
        var buffer = new TraceRingBuffer(capacity: 3);
        buffer.Add(TraceWithId("wanted"));
        buffer.Add(TraceWithId("other"));

        Assert.True(buffer.TryGet("wanted", out var trace));
        Assert.Equal("wanted", trace.TraceId);
    }

    [Fact]
    public void TryGet_AnEvictedTraceId_ReturnsFalse()
    {
        var buffer = new TraceRingBuffer(capacity: 2);
        buffer.Add(TraceWithId("evicted"));
        buffer.Add(TraceWithId("b"));
        buffer.Add(TraceWithId("c"));

        Assert.False(buffer.TryGet("evicted", out var trace));
        Assert.Null(trace);
    }

    [Fact]
    public void TryGet_AnIdThatWasNeverAdded_ReturnsFalse()
    {
        var buffer = new TraceRingBuffer(capacity: 2);
        buffer.Add(TraceWithId("a"));

        Assert.False(buffer.TryGet("never-seen", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsACapacityBelowOne(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TraceRingBuffer(capacity));

    private static RagTrace TraceWithId(string traceId) => new()
    {
        TraceId = traceId,
        StartedAt = DateTimeOffset.UnixEpoch,
        QueryHash = "hash",
    };
}
