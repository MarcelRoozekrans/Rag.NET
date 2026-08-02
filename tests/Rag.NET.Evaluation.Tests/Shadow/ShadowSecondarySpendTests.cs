using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Shadow;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The secondary's per-request spend, measured by the consumer as a before/after diff over a
/// dedicated <see cref="ICostLedger"/> — the same day-window read <c>RagAbTester.SpendAsync</c>
/// uses, honest here only because the consumer runs secondaries one at a time. The properties
/// worth pinning are the refusals: no ledger means absent rather than zero, a rollover or
/// unreadable ledger drops the figure rather than fabricating one, and a ledger problem never
/// costs the capture itself.
/// </summary>
public sealed class ShadowSecondarySpendTests
{
    [Fact]
    public async Task Consumer_ADedicatedLedger_RecordsTheSecondarysSpendDelta()
    {
        var store = new CollectingStore();
        var capture = await RunOneCaptureAsync(store, new ScriptedSpendLedger(1.00m, 1.25m));

        Assert.Equal(0.25m, capture.Secondary.Spend);
    }

    /// <summary>
    /// A failed secondary can still have cost money before it threw, and the pair that records
    /// the failure records that spend too — omitting it would make failures look free.
    /// </summary>
    [Fact]
    public async Task Consumer_ASecondaryThatThrows_StillRecordsItsSpend()
    {
        var store = new CollectingStore();
        var broken = new ScriptedRagPipeline((_, _, _) =>
            throw new InvalidOperationException("secondary broke"));
        var capture = await RunOneCaptureAsync(store, new ScriptedSpendLedger(2.00m, 2.50m), broken);

        Assert.NotNull(capture.Secondary.Failure);
        Assert.Equal(0.50m, capture.Secondary.Spend);
    }

    /// <summary>
    /// The day window empties at UTC midnight, so the second reading can come back lower than
    /// the first; the difference is then not what this run spent, and it is dropped rather than
    /// recorded — the <c>AbVariant</c> rollover rule, applied per capture.
    /// </summary>
    [Fact]
    public async Task Consumer_ALedgerThatRolledOverMidnight_DropsTheSpendRatherThanRecordingANegativeOne()
    {
        var store = new CollectingStore();
        var capture = await RunOneCaptureAsync(store, new ScriptedSpendLedger(5.00m, 1.00m));

        Assert.Null(capture.Secondary.Spend);
        Assert.NotNull(capture.Secondary.Answer);
    }

    [Fact]
    public async Task Consumer_ALedgerThatThrows_StillPersistsTheCapture_WithSpendAbsent()
    {
        var store = new CollectingStore();
        var capture = await RunOneCaptureAsync(store, new ThrowingSpendLedger());

        Assert.Null(capture.Secondary.Spend);
        Assert.NotNull(capture.Secondary.Answer);
        Assert.Null(capture.Secondary.Failure);
    }

    /// <summary>No ledger, no number: spend is absent rather than a zero that would claim the run was free.</summary>
    [Fact]
    public async Task Consumer_NoLedgerConfigured_LeavesSpendAbsent()
    {
        var store = new CollectingStore();
        var capture = await RunOneCaptureAsync(store, ledger: null);

        Assert.Null(capture.Secondary.Spend);
    }

    /// <summary>
    /// Enqueues one pending capture, runs the consumer over it with <paramref name="ledger"/>
    /// configured, and returns the single persisted pair.
    /// </summary>
    private static async Task<ShadowCapture> RunOneCaptureAsync(
        CollectingStore store,
        ICostLedger? ledger,
        IRagPipeline? secondary = null)
    {
        var queue = new ShadowCaptureQueue(new ShadowCaptureQueueOptions { Capacity = 8 });
        using var consumer = new ShadowCaptureConsumer(queue, store, new ShadowCaptureConsumerOptions
        {
            DrainTimeout = TimeSpan.FromMinutes(1),
            SecondaryCostLedger = ledger,
        });
        await consumer.StartAsync(TestContext.Current.CancellationToken);

        await queue.EnqueueAsync(Pending(secondary), TestContext.Current.CancellationToken);
        await consumer.StopAsync(CancellationToken.None)
            .WaitAsync(TestContext.Current.CancellationToken);

        return Assert.Single(store.Saved);
    }

    private static PendingShadowCapture Pending(IRagPipeline? secondary) => new(
        "q",
        Options: null,
        new ShadowVariantCapture("primary", Answer: "a", ContextTexts: ["c"], Latency: TimeSpan.FromMilliseconds(1)),
        secondary ?? new ScriptedRagPipeline(),
        "shadow",
        new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A ledger whose successive day-window readings the test scripts.</summary>
    private sealed class ScriptedSpendLedger(params decimal[] readings) : ICostLedger
    {
        private int _reads;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _reads) - 1;
            return Task.FromResult(readings[Math.Min(index, readings.Length - 1)]);
        }
    }

    private sealed class ThrowingSpendLedger : ICostLedger
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordAsync(CostEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<decimal> GetSpendAsync(CostWindow window, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the ledger is down");
    }

    private sealed class CollectingStore : IShadowCaptureStore
    {
        private readonly List<ShadowCapture> _saved = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<ShadowCapture> Saved
        {
            get
            {
                lock (_gate)
                {
                    return [.. _saved];
                }
            }
        }

        public Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _saved.Add(capture);
            }

            return Task.CompletedTask;
        }
    }
}
