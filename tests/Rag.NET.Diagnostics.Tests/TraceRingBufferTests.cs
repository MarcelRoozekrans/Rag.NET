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
        Assert.Equal(expected, buffer.Snapshot().Select(t => t.TraceId), StringComparer.Ordinal);
    }

    [Fact]
    public void Add_BelowCapacity_KeepsEverythingNewestFirst()
    {
        var buffer = new TraceRingBuffer(capacity: 5);
        buffer.Add(TraceWithId("a"));
        buffer.Add(TraceWithId("b"));

        string[] expected = ["b", "a"];
        Assert.Equal(expected, buffer.Snapshot().Select(t => t.TraceId), StringComparer.Ordinal);
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
    public async Task Add_UnderConcurrentWriters_NeverExceedsCapacity()
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

        // The bound holds under load: 2000 adds into 100 slots still yield 100 retained traces, all
        // distinct, so eviction is the only thing that ever removes one.
        //
        // This test cannot detect a missing lock and is not the one that tries to. Two writers taking
        // the same slot produce a LOST id or a null hole, never a duplicate — and neither survives
        // here, because 2000 adds over 100 slots overwrite any hole long before the assert.
        // Add_UnderConcurrentWriters_WithinCapacity_LosesNoWrite is the synchronisation test; this one
        // pins the bound.
        Assert.Equal(100, snapshot.Count);
        Assert.Equal(100, snapshot.Select(t => t.TraceId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Add_UnderConcurrentWriters_WithinCapacity_LosesNoWrite()
    {
        const int writers = 8;
        const int addsPerWriter = 32;

        // Sized so the whole workload fits: nothing is ever evicted, so a correct buffer must end
        // holding every id. That is what makes a lost write visible — under capacity, an id that is
        // absent was overwritten by a racing writer, and there is no other explanation available.
        const int capacity = writers * addsPerWriter;

        var cancellationToken = TestContext.Current.CancellationToken;

        // Repeated, because a lost write is a race and a single round can interleave benignly.
        for (var round = 0; round < 50; round++)
        {
            var buffer = new TraceRingBuffer(capacity);
            var writeTasks = new Task[writers];

            for (var w = 0; w < writers; w++)
            {
                var writer = w;
                writeTasks[w] = Task.Run(
                    () =>
                    {
                        for (var i = 0; i < addsPerWriter; i++)
                            buffer.Add(TraceWithId($"w{writer}-{i}"));
                    },
                    cancellationToken);
            }

            await Task.WhenAll(writeTasks);

            var snapshot = buffer.Snapshot();
            var retained = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < snapshot.Count; i++)
            {
                // A slot a racing writer never filled stays null, and Snapshot copies it out as one.
                Assert.NotNull(snapshot[i]);
                retained.Add(snapshot[i].TraceId);
            }

            Assert.Equal(capacity, snapshot.Count);

            for (var w = 0; w < writers; w++)
            {
                for (var i = 0; i < addsPerWriter; i++)
                    Assert.Contains($"w{w}-{i}", retained, StringComparer.Ordinal);
            }
        }
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
    public void TryGet_WhenAnIdWasAddedTwice_ReturnsTheNewerTrace()
    {
        var buffer = new TraceRingBuffer(capacity: 3);
        buffer.Add(TraceWithId("repeated") with { QueryHash = "older" });
        buffer.Add(TraceWithId("repeated") with { QueryHash = "newer" });

        // The pipeline should not reuse a trace id, and nothing in the buffer enforces that it does
        // not — an ambient activity spanning two queries is enough to produce one. Newest-wins is the
        // documented rule because a debugger opened after a request wants that request, not a stale
        // execution that happened to share the id.
        Assert.True(buffer.TryGet("repeated", out var trace));
        Assert.Equal("newer", trace.QueryHash);
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
